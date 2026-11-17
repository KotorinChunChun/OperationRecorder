#Requires -Version 7
<#
.SYNOPSIS
参照先リポジトリのPowerShell入口で対話式の手順書再発行を開始する。
.DESCRIPTION
引数はそのまま委譲する。AIからの非対話出力にはNew-OperationReport.ps1を使う。
#>
$ErrorActionPreference = 'Stop'
$repo = & (Join-Path $PSScriptRoot 'Resolve-OperationRecorderRepository.ps1')
& (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $repo 'tools/legacy-recorder/Reissue-OperationReport.ps1') @args
exit $LASTEXITCODE
