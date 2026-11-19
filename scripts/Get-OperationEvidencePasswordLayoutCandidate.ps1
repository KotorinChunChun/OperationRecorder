#Requires -Version 7.6
<#
.SYNOPSIS
OperationRecorderのパスワード欄について、撮影設定候補に必要な構造と座標を読み取る。
.DESCRIPTION
稼働中のウィンドウを対象に、期限付きの独立プロセスで構造だけを確認する。
公開ラベルは既知の文字列と内部で照合する。結果はcandidate-onlyで、設定への登録は行わない。
DLL付きのframework-dependentビルドが必要。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)]$Target,
    [Parameter(Mandatory)][string]$PublicLabel,
    [Parameter(Mandatory)][string]$FieldClassPrefix,
    [ValidateRange(200,30000)][int]$TimeoutMilliseconds = 5000
)

$ErrorActionPreference = 'Stop'
try {
    $exe = (Resolve-Path -LiteralPath $Executable).Path
    $dll = Join-Path (Split-Path -Parent $exe) 'OperationRecorder.dll'
    $loaded = [AppDomain]::CurrentDomain.GetAssemblies() |
        Where-Object { $_.GetName().Name -eq 'OperationRecorder' } | Select-Object -First 1
    if ($loaded -and $loaded.Location -ine $dll) { return [pscustomobject]@{ status = 'layout-unproven' } }
    if (-not $loaded) { Add-Type -LiteralPath $dll }
} catch {
    return [pscustomobject]@{ status = 'layout-unproven' }
}

try {
    $startTime = $Target.processStartTimeUtc
    if ($startTime -is [DateTimeOffset] -or $startTime -is [DateTime]) { $startTime = $startTime.ToString('o') }
    $request = @{
        target = @{
            pid = $Target.pid
            processStartTimeUtc = $startTime
            exePath = $Target.exePath
            windowsSessionId = $Target.windowsSessionId
            windowHandle = $Target.windowHandle
        }
        publicLabel = $PublicLabel
        fieldClassPrefix = $FieldClassPrefix
    } | ConvertTo-Json -Depth 4 -Compress -WarningAction Stop
} catch {
    return [pscustomobject]@{ status = 'invalid-request' }
}

try {
    [OperationRecorderApp.ExternalEvidence.PasswordLayoutCandidate]::ObserveJsonAsync(
        $exe, $request, $TimeoutMilliseconds).GetAwaiter().GetResult() | ConvertFrom-Json
} catch {
    [pscustomobject]@{ status = 'layout-unproven' }
}
