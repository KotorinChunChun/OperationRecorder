#Requires -Version 7
<#
.SYNOPSIS
このPCで使用するOperationRecorderリポジトリの絶対パスを返す。
#>
[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$ConfigurationDirectory = (Join-Path $env:LOCALAPPDATA 'OperationRecorderSkill')
)
$ErrorActionPreference = 'Stop'
if (-not $RepositoryRoot) {
    $RepositoryRoot = $env:OPERATION_RECORDER_ROOT
}
if (-not $RepositoryRoot) {
    $configPath = Join-Path $ConfigurationDirectory 'repository.json'
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw 'OperationRecorderの参照先が未設定です。リポジトリのscripts/Install-OperationRecorderSkill.ps1を実行してください。導入手順: https://github.com/KotorinChunChun/OperationRecorder/blob/main/docs/ai-skill.md'
    }
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    if ($config.schemaVersion -ne 1 -or -not $config.repositoryRoot) {
        throw "OperationRecorderの参照先設定が不正です: $configPath"
    }
    $RepositoryRoot = [string]$config.repositoryRoot
}
if (-not [IO.Path]::IsPathFullyQualified($RepositoryRoot)) {
    throw "OperationRecorderの参照先は絶対パスで指定してください: $RepositoryRoot"
}
$resolved = (Resolve-Path -LiteralPath $RepositoryRoot -ErrorAction Stop).Path
foreach ($relative in @(
    'OperationRecorder.slnx', 'src/OperationRecorder/Engine/Recorder.cs',
    'templates/user.md', 'docs/USERS_GUIDE.md',
    'tools/legacy-recorder/Start-OperationRecording.ps1',
    'tools/legacy-recorder/New-OperationReport.ps1',
    'tools/legacy-recorder/Reissue-OperationReport.ps1'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $resolved $relative) -PathType Leaf)) {
        throw "OperationRecorderリポジトリに必要なファイルがありません: $resolved/$relative"
    }
}
$resolved
