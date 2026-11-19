#Requires -Version 7.6
<#
.SYNOPSIS
OperationRecorderの外部証拠API用クライアント。実処理は専用broker/workerで行う。
.DESCRIPTION
framework-dependentビルドのOperationRecorder.exeと同じ場所のDLLを読み込む。
資格情報や入力値を記録要求へ渡さない。対象アプリの終了監視は呼出し側が行う。
#>
$ErrorActionPreference = 'Stop'
function Import-OperationEvidenceAssembly {
    param([Parameter(Mandatory)][string]$Executable)
    $exe = (Resolve-Path -LiteralPath $Executable).Path
    $dll = Join-Path (Split-Path -Parent $exe) 'OperationRecorder.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw 'このPSクライアントにはDLL付きのframework-dependentビルドが必要です。docs/external-evidence.mdを参照してください。' }
    $loaded = [AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq 'OperationRecorder' } | Select-Object -First 1
    if ($loaded -and $loaded.Location -ine $dll) { throw '別のOperationRecorder DLLが読み込まれています。新しいpwshセッションで実行してください。' }
    if (-not $loaded) { Add-Type -Path $dll }
    return $exe
}
function New-OperationEvidenceSession {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][hashtable]$Target,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [hashtable]$Policy = @{},
        [Guid]$SessionId = [Guid]::NewGuid(),
        [ValidateSet(1,2)][int]$SchemaVersion = 1,
        [hashtable]$GroupPolicy
    )
    $exe = Import-OperationEvidenceAssembly -Executable $Executable
    $connection = [OperationRecorderApp.ExternalEvidence.EvidenceConnection]::StartAsync($exe).GetAwaiter().GetResult()
    if ($SchemaVersion -eq 1 -and $GroupPolicy) { $connection.Dispose(); throw 'GroupPolicyにはSchemaVersion 2が必要です。' }
    $session = [pscustomobject]@{ Connection = $connection; SessionId = $SessionId; Closed = $false; SchemaVersion = $SchemaVersion; WindowGraph = $null; Capabilities = @() }
    try {
        $request = @{
            method = 'StartSession'; target = $Target; policy = $Policy
            outputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
        }
        if ($SchemaVersion -eq 2) { $request.groupPolicy = $(if ($GroupPolicy) { $GroupPolicy } else { @{} }) }
        $reply = Send-OperationEvidenceRequest -Session $session -Request $request
        if ($reply.status -ne 'started') { throw "記録を開始できません: $($reply.status)" }
        if ($SchemaVersion -eq 2) { $session.WindowGraph = $reply.windowGraph }
        $session.Capabilities = @($reply.capabilities)
        return $session
    } catch { $connection.Dispose(); throw }
}
function Test-OperationEvidenceRoiProfile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string]$ProfileId,
        [Parameter(Mandatory)][int]$Revision,
        [Parameter(Mandatory)][string]$ProfileSha256,
        [string]$TargetExecutable
    )
    $exe = Import-OperationEvidenceAssembly -Executable $Executable
    $selection = [OperationRecorderApp.ExternalEvidence.RoiProfileSelection]::new($ProfileId, $Revision, $ProfileSha256)
    $profile = [OperationRecorderApp.ExternalEvidence.ApprovedRoiCatalog]::Load($selection, (Split-Path -Parent $exe))
    if ($TargetExecutable) {
        $targetHash = (Get-FileHash -LiteralPath $TargetExecutable -Algorithm SHA256).Hash
        if ($targetHash -ine $profile.AppSha256) { throw 'profile-app-mismatch' }
    }
    [pscustomobject]@{
        ProfileId = $profile.ProfileId; Revision = $profile.Revision; ProfileSha256 = $ProfileSha256.ToLowerInvariant()
        AppSha256 = $profile.AppSha256; SceneCount = $profile.Scenes.Count
    }
}
function Test-OperationEvidencePasswordProfile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string]$ProfileId,
        [Parameter(Mandatory)][int]$Revision,
        [Parameter(Mandatory)][string]$ProfileSha256,
        [string]$TargetExecutable
    )
    $exe = Import-OperationEvidenceAssembly -Executable $Executable
    $selection = [OperationRecorderApp.ExternalEvidence.PasswordProfileSelection]::new($ProfileId, $Revision, $ProfileSha256)
    $profile = [OperationRecorderApp.ExternalEvidence.PasswordEvidenceCatalog]::Load($selection, (Split-Path -Parent $exe))
    if ($TargetExecutable) {
        $target = (Resolve-Path -LiteralPath $TargetExecutable).Path
        $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        if (-not [OperationRecorderApp.ExternalEvidence.PasswordEvidenceCatalog]::MatchesApplication($profile, $target, $hash)) {
            throw 'profile-app-mismatch'
        }
    }
    [pscustomobject]@{
        ProfileId = $profile.ProfileId; Revision = $profile.Revision; ProfileSha256 = $ProfileSha256.ToLowerInvariant()
        AppScope = $profile.AppScope; AppSha256 = $profile.AppSha256; AppFileVersion = $profile.AppFileVersion; SceneCount = $profile.Scenes.Count
    }
}
function Send-OperationEvidenceRequest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Session,
        [Parameter(Mandatory)][hashtable]$Request,
        [ValidateRange(200,30000)][int]$TimeoutMilliseconds = 10000
    )
    if ($Session.Closed) { throw '記録セッションは終了しています。' }
    $body = @{} + $Request
    $body.schemaVersion = $(if ($Session.PSObject.Properties['SchemaVersion']) { $Session.SchemaVersion } else { 1 }); $body.sessionId = $Session.SessionId.ToString()
    if (-not $body.ContainsKey('requestId')) { $body.requestId = [Guid]::NewGuid().ToString() }
    if (-not $body.ContainsKey('deadlineUtc')) { $body.deadlineUtc = [DateTimeOffset]::UtcNow.AddMilliseconds($TimeoutMilliseconds - 50).ToString('o') }
    $json = $body | ConvertTo-Json -Depth 12 -Compress
    $Session.Connection.SendJsonAsync($json, $TimeoutMilliseconds).GetAwaiter().GetResult() | ConvertFrom-Json
}
function Get-OperationEvidenceSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Session,
        [Guid]$RequestId = [Guid]::NewGuid(),
        [ValidateRange(200,30000)][int]$TimeoutMilliseconds = 5000
    )
    if ($Session.PSObject.Properties['SchemaVersion'].Value -ne 2) {
        throw '単発取得にはSchemaVersion 2の外部証拠セッションが必要です。'
    }
    if ($RequestId -eq [Guid]::Empty) { throw 'RequestIdは空にできません。' }
    Send-OperationEvidenceRequest -Session $Session -Request @{
        method = 'CaptureSnapshot'; requestId = $RequestId.ToString()
    } -TimeoutMilliseconds $TimeoutMilliseconds
}
function Invoke-OperationEvidenceStep {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Session,
        [Parameter(Mandatory)][Guid]$StepId,
        [Parameter(Mandatory)][ValidateSet('invoke','set-value','native-message','select','observe','custom')][string]$OperationName,
        [Parameter(Mandatory)][scriptblock]$Action,
        [hashtable]$ExpectedStateMetadata = @{},
        [hashtable]$ActualStateMetadata = @{},
        [ValidateRange(200,30000)][int]$TimeoutMilliseconds = 10000
    )
    $before = Send-OperationEvidenceRequest -Session $Session -Request @{
        method='BeginStep'; stepId=$StepId.ToString(); operationName=$OperationName; expectedStateMetadata=$ExpectedStateMetadata
    } -TimeoutMilliseconds $TimeoutMilliseconds
    if ($before.status -ne 'before-saved' -or $before.operationAllowed -ne $true) { throw "操作前証拠が未確定です。操作は実行していません: $($before.status)" }
    try { $null = & $Action }
    catch {
        # No original exception/inner exception is sent or wrapped: it may contain an input value.
        try { $null = Send-OperationEvidenceRequest -Session $Session -Request @{method='RecordFailure';stepId=$StepId.ToString();errorCode='operation-failed'} -TimeoutMilliseconds 1000 } catch { }
        throw '操作に失敗しました。記録工程は未完了です。呼出し側の対象終了方針に従ってください。'
    }
    $after = Send-OperationEvidenceRequest -Session $Session -Request @{
        method='EndStep'; stepToken=$before.stepToken; outcome='success'; actualStateMetadata=$ActualStateMetadata
    } -TimeoutMilliseconds $TimeoutMilliseconds
    if ($after.status -ne 'after-saved') { throw "操作後証拠が未確定です。操作を再実行しないでください: $($after.status)" }
    return $after
}
function Close-OperationEvidenceSession {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Session, [ValidateRange(200,30000)][int]$TimeoutMilliseconds = 1000)
    if ($Session.Closed) { return }
    try { Send-OperationEvidenceRequest -Session $Session -Request @{method='StopSession'} -TimeoutMilliseconds $TimeoutMilliseconds }
    finally { $Session.Closed = $true; $Session.Connection.Dispose() }
}
Export-ModuleMember -Function New-OperationEvidenceSession,Send-OperationEvidenceRequest,Test-OperationEvidenceRoiProfile,Test-OperationEvidencePasswordProfile,Get-OperationEvidenceSnapshot,Invoke-OperationEvidenceStep,Close-OperationEvidenceSession
