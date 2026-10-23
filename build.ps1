<#
.SYNOPSIS
操作記録ツールを配布用にビルドする。.NET を同梱した単体の exe を作り、テンプレート・マニュアルと一緒に zip にまとめる。

.DESCRIPTION
出力:
  dist\OperationRecorder\                 … 展開後と同じ中身(OperationRecorder.exe / templates\ / MANUAL.md)
  dist\OperationRecorder-<版>-win-x64.zip  … 配布用

記録エンジン・報告書作成・テンプレート・マニュアルは、子内の operation-recorder
(Claude のスキル)のファイルをそのまま取り込む(-SkillDir で場所を変えられる)。

配布用を作る前に、自動テスト(OperationRecorderApp.Tests。画面は操作しない)を実行し、失敗したら止まる。

.EXAMPLE
pwsh -File build.ps1
pwsh -File build.ps1 -SkipTests
#>
param(
    [string]$SkillDir = (Join-Path $PSScriptRoot 'operation-recorder'),
    [string]$Runtime = 'win-x64',
    # 自動テストを省略する
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $SkipTests) {
    dotnet test (Join-Path $PSScriptRoot 'OperationRecorderApp.Tests/OperationRecorderApp.Tests.csproj') -c Release -nologo -p:SkillDir="$SkillDir"
    if ($LASTEXITCODE -ne 0) { throw "自動テストが失敗しました(終了コード $LASTEXITCODE)。配布用は作っていません" }
}

$version = ([xml](Get-Content .\OperationRecorderApp.csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$dist = Join-Path $PSScriptRoot 'dist'
$out = Join-Path $dist 'OperationRecorder'
# フォルダごと消すと、エクスプローラーなどで開いているときに失敗するので、中身だけを消す
if (Test-Path $out) { Get-ChildItem $out -Force | Remove-Item -Recurse -Force }

dotnet publish .\OperationRecorderApp.csproj -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:SkillDir="$SkillDir" -o $out -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish が失敗しました(終了コード $LASTEXITCODE)" }

$zip = Join-Path $dist "OperationRecorder-$version-$Runtime.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip

Get-ChildItem $out -Recurse -File | ForEach-Object { "{0,10:N0} KB  {1}" -f ($_.Length / 1KB), $_.FullName.Substring($out.Length + 1) }
"zip: {0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)
