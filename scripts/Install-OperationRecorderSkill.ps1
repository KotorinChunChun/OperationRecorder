#Requires -Version 7
<#
.SYNOPSIS
ローカルリポジトリを参照するOperationRecorderスキルを導入・更新する。
.DESCRIPTION
既定の導入先はCodex。SkillsDirectoryにClaudeまたは同期カタログのskillsディレクトリも指定できる。
既存の変更対象ファイルをバックアップし、旧資材と他のスキルは削除しない。
ネットワーク通信、Git操作、アプリ起動は行わない。
.PARAMETER RepositoryRoot
参照するリポジトリ。既定はこのスクリプトを含むチェックアウト。
.PARAMETER SkillsDirectory
operation-recorderを置く親フォルダー。既定はCODEX_HOME/skillsまたはユーザーの.codex/skills。
.PARAMETER ConfigurationDirectory
PC固有の参照先とバックアップの保存先。通常は変更しない。
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..'),
    [string]$SkillsDirectory = $(if ($env:CODEX_HOME) { Join-Path $env:CODEX_HOME 'skills' } else { Join-Path $HOME '.codex/skills' }),
    [string]$ConfigurationDirectory = (Join-Path $env:LOCALAPPDATA 'OperationRecorderSkill')
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$source = Join-Path $root 'skills/operation-recorder'
$resolver = Join-Path $source 'scripts/Resolve-OperationRecorderRepository.ps1'
if (-not (Test-Path -LiteralPath $resolver -PathType Leaf)) {
    throw "配布用スキルがありません: $source"
}
$root = & $resolver -RepositoryRoot $root
$skillsPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($SkillsDirectory)
$configDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ConfigurationDirectory)
$destination = Join-Path $skillsPath 'operation-recorder'
if ([IO.Path]::GetFullPath($source).TrimEnd('\', '/') -ieq [IO.Path]::GetFullPath($destination).TrimEnd('\', '/')) {
    throw '配布スキルの正本と導入先が同一です。Codex/Claudeまたは同期カタログのskillsを指定してください。'
}
$files = @(Get-ChildItem -LiteralPath $source -Recurse -File)
$configPath = Join-Path $configDirectory 'repository.json'
if (-not $PSCmdlet.ShouldProcess("$destination および $configPath", '既存ファイルを保全してリポジトリ参照スキルを導入')) { return }

$backupDirectory = Join-Path $configDirectory ('backups/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
    $existing = Join-Path $destination $relative
    if (Test-Path -LiteralPath $existing -PathType Leaf) {
        $backupPath = Join-Path $backupDirectory "skill/$relative"
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backupPath) | Out-Null
        Copy-Item -LiteralPath $existing -Destination $backupPath -Force
    }
}
if (Test-Path -LiteralPath $configPath -PathType Leaf) {
    New-Item -ItemType Directory -Force -Path $backupDirectory | Out-Null
    Copy-Item -LiteralPath $configPath -Destination (Join-Path $backupDirectory 'repository.json') -Force
}
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
    $target = Join-Path $destination $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target -Force
}
New-Item -ItemType Directory -Force -Path $configDirectory | Out-Null
[ordered]@{ schemaVersion = 1; repositoryRoot = $root } | ConvertTo-Json |
    Set-Content -LiteralPath $configPath -Encoding utf8NoBOM
if ($env:OPERATION_RECORDER_ROOT -and $env:OPERATION_RECORDER_ROOT -ine $root) {
    Write-Warning 'OPERATION_RECORDER_ROOTが別の参照先を指定しています。スキル実行時は環境変数が優先されます。'
}
[pscustomobject]@{
    SkillDirectory = $destination
    RepositoryRoot = $root
    Configuration = $configPath
    BackupDirectory = $(if (Test-Path -LiteralPath $backupDirectory) { $backupDirectory } else { $null })
}
