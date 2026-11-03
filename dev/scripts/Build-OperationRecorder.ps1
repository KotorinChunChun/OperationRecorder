<#
.SYNOPSIS
.NETを同梱する単体EXEと関連資産を、上書きしない実行別フォルダーへ生成する。
.DESCRIPTION
出力はrelease/<実行識別子>/OperationRecorder/と同じフォルダーのZIP。
既定ではCore試験を先に実行する。実入力・撮影は別途Desktop試験を明示する。
正式インストーラーの生成・公開は親の共通配布スクリプトを使用する。
.EXAMPLE
pwsh -File dev/scripts/Build-OperationRecorder.ps1 -SkipTests
#>
[CmdletBinding()]
param(
    [ValidatePattern('^win-(x64|arm64)$')][string]$Runtime = 'win-x64',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$project = Join-Path $repoRoot 'src/OperationRecorder/OperationRecorderApp.csproj'
if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot 'Test-OperationRecorder.ps1') -Suite Core
    if ($LASTEXITCODE -ne 0) { throw "自動テストが失敗しました(終了コード $LASTEXITCODE)。配布用は作っていません。" }
}
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
$runRoot = Join-Path $repoRoot "release/$runId"
$out = Join-Path $runRoot 'OperationRecorder'
New-Item -ItemType Directory -Path $out -Force | Out-Null
& dotnet publish $project -c Release -r $Runtime --self-contained true `
    --artifacts-path (Join-Path $repoRoot 'artifacts/portable') `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -o $out -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publishが失敗しました(終了コード $LASTEXITCODE)。生成途中のファイルは$runRootに保存しています。" }
$zip = Join-Path $runRoot "OperationRecorder-$version-$Runtime.zip"
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
Get-ChildItem -LiteralPath $out -Recurse -File | ForEach-Object { "{0,10:N0} KB  {1}" -f ($_.Length / 1KB), $_.FullName.Substring($out.Length + 1) }
"zip: {0}  ({1:N1} MB)" -f $zip, ((Get-Item -LiteralPath $zip).Length / 1MB)
