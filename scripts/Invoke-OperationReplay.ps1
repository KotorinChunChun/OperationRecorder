[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Config,
    [ValidateSet('check','run','watch')][string]$Mode = 'check',
    [string]$Executable = (Join-Path $PSScriptRoot '../OperationRecorder.exe'),
    [string]$LogDirectory,
    [string]$Result,
    [switch]$Now
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$exePath = (Resolve-Path -LiteralPath $Executable).Path
$configPath = (Resolve-Path -LiteralPath $Config).Path
$replayArguments = @('--replay', $Mode, '--config', $configPath)
if ($LogDirectory) { $replayArguments += @('--logs', $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($LogDirectory)) }
if ($Result) { $replayArguments += @('--result', $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Result)) }
if ($Now) { $replayArguments += '--now' }
# WinExeでも終了まで待ち、UTF-8のJSONと終了コードをそのまま返す。
& $exePath @replayArguments | Write-Output
exit $LASTEXITCODE
