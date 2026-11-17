#Requires -Version 7
<#
.SYNOPSIS
スキルの導入・参照先解決・コマンド委譲を隔離フォルダーで検証する。記録やGUI操作は行わない。
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$installer = Join-Path $repo 'scripts/Install-OperationRecorderSkill.ps1'
$testRoot = Join-Path $repo ('artifacts/skill-tests/' + [guid]::NewGuid().ToString('N'))
$configDirectory = Join-Path $testRoot 'configuration'
$skillsDirectory = Join-Path $testRoot 'skills'
$installed = Join-Path $skillsDirectory 'operation-recorder'
$resolver = Join-Path $installed 'scripts/Resolve-OperationRecorderRepository.ps1'
$savedEnvironment = @{}
foreach ($key in @('OPERATION_RECORDER_ROOT', 'LOCALAPPDATA', 'CODEX_HOME')) {
    $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
}
$script:checks = 0
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Expect-Failure([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Assert $failed $Message
}
try {
    $env:OPERATION_RECORDER_ROOT = $null
    $whatIfDirectory = Join-Path $testRoot 'what-if'
    & $installer -SkillsDirectory (Join-Path $whatIfDirectory 'skills') -ConfigurationDirectory (Join-Path $whatIfDirectory 'config') -WhatIf
    Assert (-not (Test-Path -LiteralPath $whatIfDirectory)) 'WhatIfでファイルが作成された'

    $first = & $installer -SkillsDirectory $skillsDirectory -ConfigurationDirectory $configDirectory
    Assert ($first.RepositoryRoot -eq $repo) '初回導入の参照先が不正'
    Assert (-not $first.BackupDirectory) '初回導入に不要なバックアップがある'
    Assert ((& $resolver -ConfigurationDirectory $configDirectory) -eq $repo) '設定から参照先を解決できない'
    Expect-Failure { & $resolver -ConfigurationDirectory (Join-Path $testRoot 'missing-config') } '未導入状態を拒否しない'

    Set-Content -LiteralPath (Join-Path $installed 'SKILL.md') -Value 'previous-skill' -Encoding utf8NoBOM
    $retained = Join-Path $installed 'legacy-asset.txt'
    Set-Content -LiteralPath $retained -Value 'keep' -Encoding utf8NoBOM
    $configBefore = [IO.File]::ReadAllText((Join-Path $configDirectory 'repository.json'))
    $update = & $installer -SkillsDirectory $skillsDirectory -ConfigurationDirectory $configDirectory
    Assert ((Get-Content -LiteralPath (Join-Path $update.BackupDirectory 'skill/SKILL.md') -Raw).Trim() -eq 'previous-skill') '旧スキルのバックアップが不正'
    Assert ([IO.File]::ReadAllText((Join-Path $update.BackupDirectory 'repository.json')) -ceq $configBefore) '旧設定のバックアップが不正'
    Assert ((Get-Content -LiteralPath $retained -Raw).Trim() -eq 'keep') '旧資材が失われた'

    $env:CODEX_HOME = Join-Path $testRoot 'custom-codex'
    $custom = & $installer -ConfigurationDirectory $configDirectory
    Assert ($custom.SkillDirectory -eq (Join-Path $env:CODEX_HOME 'skills/operation-recorder')) 'CODEX_HOMEを尊重しない'

    # 空白・日本語を含む別のチェックアウトを用意する。実処理の代わりに受け取った引数を返す。
    $fixture = Join-Path $testRoot '別の repository'
    foreach ($relative in @('OperationRecorder.slnx', 'src/OperationRecorder/Engine/Recorder.cs', 'templates/user.md', 'docs/USERS_GUIDE.md')) {
        $target = Join-Path $fixture $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Set-Content -LiteralPath $target -Value 'fixture' -Encoding utf8NoBOM
    }
    $stub = @'
param([string]$SessionDir, [string[]]$Template, [switch]$Open, [int]$ReturnCode)
[ordered]@{ SessionDir = $SessionDir; Template = $Template; Open = [bool]$Open } | ConvertTo-Json -Compress
exit $ReturnCode
'@
    foreach ($name in @('Start-OperationRecording', 'New-OperationReport', 'Reissue-OperationReport')) {
        $target = Join-Path $fixture "tools/legacy-recorder/$name.ps1"
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Set-Content -LiteralPath $target -Value $stub -Encoding utf8NoBOM
    }
    $env:OPERATION_RECORDER_ROOT = $fixture
    Assert ((& $resolver -ConfigurationDirectory $configDirectory) -eq $fixture) '環境変数が設定より優先されない'
    $sessionArgument = 'D:/記録 path/引用符 "テスト"'
    foreach ($name in @('Start-OperationRecording', 'New-OperationReport', 'Reissue-OperationReport')) {
        $output = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $installed "scripts/$name.ps1") -SessionDir $sessionArgument -Template user,ai -Open -ReturnCode 23
        $code = $LASTEXITCODE
        Assert ($code -eq 23) "$name が終了コードを委譲しない: $code"
        $received = $output | ConvertFrom-Json
        Assert ($received.SessionDir -ceq $sessionArgument -and $received.Template[0] -eq 'user,ai' -and $received.Open) "$name が引数を維持しない"
    }

    $env:OPERATION_RECORDER_ROOT = Join-Path $testRoot 'nonexistent'
    Expect-Failure { & $resolver -ConfigurationDirectory $configDirectory } '不正な環境変数から設定へフォールバックした'
    $env:OPERATION_RECORDER_ROOT = 'relative/path'
    Expect-Failure { & $resolver -ConfigurationDirectory $configDirectory } '相対参照を拒否しない'
    $env:OPERATION_RECORDER_ROOT = $null
    Set-Content -LiteralPath (Join-Path $configDirectory 'repository.json') -Value '{broken' -Encoding utf8NoBOM
    Expect-Failure { & $resolver -ConfigurationDirectory $configDirectory } '不正なJSONを拒否しない'
    [ordered]@{ schemaVersion = 99; repositoryRoot = $repo } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $configDirectory 'repository.json')
    Expect-Failure { & $resolver -ConfigurationDirectory $configDirectory } '不明な設定形式を拒否しない'
    [pscustomobject]@{ Checks = $script:checks; Result = 'passed'; Artifacts = $testRoot }
} finally {
    foreach ($key in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key], 'Process')
    }
}
