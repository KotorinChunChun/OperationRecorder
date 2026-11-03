[CmdletBinding(DefaultParameterSetName='New')]
param(
    [Parameter(Mandatory, ParameterSetName='New')][string]$InputFile,
    [Parameter(Mandatory, ParameterSetName='New')][string]$OutputDirectory,
    [Parameter(Mandatory, ParameterSetName='Existing')][string]$Bundle,
    [string]$Executable = (Join-Path $PSScriptRoot '../OperationRecorder.exe'),
    [string]$CodexExecutable = 'codex',
    [string]$Model,
    [string]$DraftOutput,
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$exe = (Resolve-Path -LiteralPath $Executable).Path
if ($PSCmdlet.ParameterSetName -eq 'New') {
    $bundleRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
    & $exe --replay learn-prepare --input (Resolve-Path -LiteralPath $InputFile).Path --out $bundleRoot | Write-Output
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} else { $bundleRoot = (Resolve-Path -LiteralPath $Bundle).Path }
if ($PrepareOnly) { Write-Output "分析資料を作成しました：$bundleRoot"; exit 0 }

$analysis = Join-Path $bundleRoot 'analysis'
$manifest = Get-Content -LiteralPath (Join-Path $bundleRoot 'manifest.json') -Raw | ConvertFrom-Json
$observations = Join-Path $analysis 'observations.json'
if ((Get-FileHash -LiteralPath $observations -Algorithm SHA256).Hash -ne $manifest.observationsSha256) { throw '比較データが変更されています。bundleを作り直してください。' }
if (-not $DraftOutput) { $DraftOutput = Join-Path $bundleRoot 'draft.json' }
$draft = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($DraftOutput)
if ((Test-Path -LiteralPath $draft) -or (Test-Path -LiteralPath ($draft + '.review.json'))) { throw '既存の案を上書きしません。新しい-DraftOutputを指定してください。' }
$response = Join-Path $bundleRoot ('proposal-' + [guid]::NewGuid().ToString('N') + '.json')
$schema = Join-Path $analysis 'response.schema.json'
$prompt = [IO.File]::ReadAllText((Join-Path $analysis 'prompt.md')) + "`n<recording_data>`n" + [IO.File]::ReadAllText($observations) + "`n</recording_data>"
# 元記録・入力値はstdinへ渡さない。analysis以外を参照せずツールを使わないようプロンプトでも制限する。
# read-onlyはモデルの生成物をコードとして実行させるための権限ではない。
$codexArguments = @('exec','--sandbox','read-only','--skip-git-repo-check','--ephemeral','--ignore-user-config',
    '--cd',$analysis,'--output-schema',$schema,'--output-last-message',$response,'--color','never')
if ($Model) { $codexArguments += @('--model',$Model) }
$codexArguments += '-'
$previousEncoding = $OutputEncoding
try {
    $OutputEncoding = [Text.UTF8Encoding]::new($false)
    $prompt | & $CodexExecutable @codexArguments | Out-Host
    $codexExit = $LASTEXITCODE
} finally { $OutputEncoding = $previousEncoding }
if ($codexExit -ne 0) { throw "Codex CLIが完了しませんでした（終了 $codexExit）。案は適用していません。" }
if (-not (Test-Path -LiteralPath $response -PathType Leaf)) { throw 'Codex CLIの最終JSONがありません。' }
& $exe --replay learn-apply --bundle $bundleRoot --proposal $response --out $draft | Write-Output
exit $LASTEXITCODE
