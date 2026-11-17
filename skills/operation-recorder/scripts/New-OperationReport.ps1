#Requires -Version 7
<#
.SYNOPSIS
参照先リポジトリのPowerShell入口で記録からMarkdownを出力する。
.DESCRIPTION
引数はそのまま委譲する。詳しい引数は参照先の同名スクリプトのGet-Helpを参照する。
#>
$ErrorActionPreference = 'Stop'
$repo = & (Join-Path $PSScriptRoot 'Resolve-OperationRecorderRepository.ps1')
& (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $repo 'tools/legacy-recorder/New-OperationReport.ps1') @args
exit $LASTEXITCODE
