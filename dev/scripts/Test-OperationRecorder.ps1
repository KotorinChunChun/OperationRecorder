[CmdletBinding()]
param(
    [string]$Suite = 'Core',
    [string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [switch]$NoBuild,
    [switch]$List
)

# 画面操作ツールを使わない一括試験。実入力は明示指定のDesktopだけで行う。
$ErrorActionPreference = 'Stop'
# nativeの非0はTRXと合わせて分類する。呼出元の設定で先に例外へ変換しない。
$PSNativeCommandUseErrorActionPreference = $false
if ($Suite -notin @('Core', 'Desktop', 'All') -or $Configuration -notin @('Debug', 'Release')) {
    [Console]::Error.WriteLine('SuiteはCore/Desktop/All、ConfigurationはDebug/Releaseを指定してください。')
    exit 2
}
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
[xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'OperationRecorderApp.csproj') -Raw
$version = [string]($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
$history = Join-Path $repoRoot "dev/history/v$version"
if ($List) {
    Get-Content -LiteralPath (Join-Path $history 'test-cases.json') -Raw
    exit 0
}
$started = [DateTime]::UtcNow
$summary = [ordered]@{ schemaVersion = 1; version = $version; status = 'failed'; exitCode = 1; startedUtc = $started.ToString('o'); endedUtc = $null; suites = @() }
$savedEnvironment = @{}
$exitCode = 1
$runRoot = $null

function Invoke-Dotnet([string[]]$Arguments, [string]$LogName) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $runRoot $LogName) | ForEach-Object { Write-Host $_ }
    return $LASTEXITCODE
}

function Invoke-Suite([string]$Name) {
    $entry = [ordered]@{ name = $Name; status = 'failed'; total = 0; passed = 0; failed = 0; skipped = 0; trx = $null; reason = $null }
    if ($Name -eq 'Desktop') {
        if (-not $IsWindows -or -not [Environment]::UserInteractive) {
            $entry.status = 'blocked'; $entry.reason = 'DESKTOP_BLOCKED: 対話可能なWindowsデスクトップが必要です。'; return $entry
        }
        $existing = @(Get-Process -Name OperationRecorder -ErrorAction SilentlyContinue)
        if ($existing.Count -gt 0) {
            $entry.status = 'blocked'; $entry.reason = 'DESKTOP_BLOCKED: 起動中のOperationRecorderがあります。既存アプリへの入力と撮影を避けるため実行しません。'; return $entry
        }
        if (-not (Test-Path -LiteralPath $fixture -PathType Leaf)) { throw '専用fixtureが未ビルドです。-NoBuildを外して実行してください。' }
        $env:OPREC_DESKTOP_TESTS = '1'
        $env:OPREC_FIXTURE_EXE = $fixture
        $env:OPREC_RECORDING_ARTIFACTS = Join-Path $runRoot 'recording'
    }
    else { $env:OPREC_DESKTOP_TESTS = '0' }
    $resultDir = Join-Path $runRoot $Name.ToLowerInvariant()
    [void][IO.Directory]::CreateDirectory($resultDir)
    $entry.trx = Join-Path $resultDir 'tests.trx'
    $filter = if ($Name -eq 'Desktop') { 'Category=Desktop' } else { 'Category!=Desktop' }
    $testExit = Invoke-Dotnet @('test', $testProject, '-c', $Configuration, '--artifacts-path', $artifacts,
        '--no-build', '--no-restore', '--filter', $filter, '--logger', 'trx;LogFileName=tests.trx', '--results-directory', $resultDir) ($Name.ToLowerInvariant() + '.log')
    if (-not (Test-Path -LiteralPath $entry.trx -PathType Leaf)) { $entry.reason = "TRXが作成されませんでした（終了コード $testExit）。"; return $entry }
    [xml]$trx = Get-Content -LiteralPath $entry.trx -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    $entry.total = [int]$counters.total; $entry.passed = [int]$counters.passed; $entry.failed = [int]$counters.failed; $entry.skipped = [int]$counters.notExecuted
    $failedResults = @($trx.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' })
    $blockedResults = @($failedResults | Where-Object { $_.Output.ErrorInfo.Message -like '*DESKTOP_BLOCKED:*' })
    if ($Name -eq 'Desktop' -and $blockedResults.Count -gt 0 -and $failedResults.Count -eq $blockedResults.Count -and $entry.skipped -eq 0) {
        $entry.status = 'blocked'; $entry.reason = 'DESKTOP_BLOCKED: デスクトップ条件を満たさない試験があります。詳細はTRXを参照してください。'
    }
    elseif ($testExit -eq 0 -and $entry.total -gt 0 -and $entry.total -eq $entry.passed -and $entry.skipped -eq 0) { $entry.status = 'passed' }
    else { $entry.reason = "失敗・未実行またはテスト未検出があります（終了コード $testExit）。" }
    return $entry
}

try {
    $logsRoot = [IO.Path]::GetFullPath((Join-Path $history 'logs')).TrimEnd('\', '/')
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $logsRoot ('automated-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
    $runRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
    if (-not $runRoot.StartsWith($logsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { $exitCode = 2; throw 'OutputDirectoryは対象版のlogs配下の実行フォルダーを指定してください。' }
    for ($checkPath = $runRoot; $checkPath; $checkPath = [IO.Path]::GetDirectoryName($checkPath)) {
        if ((Test-Path -LiteralPath $checkPath) -and ((Get-Item -LiteralPath $checkPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { $exitCode = 2; throw 'ログ保存先にリンクを使用できません。' }
    }
    if (Test-Path -LiteralPath (Join-Path $runRoot 'summary.json')) { $exitCode = 2; throw '既存の試験結果を上書きしない保存先を指定してください。' }
    [void][IO.Directory]::CreateDirectory($runRoot)
    foreach ($name in @('OPREC_DESKTOP_TESTS', 'OPREC_FIXTURE_EXE', 'OPREC_RECORDING_ARTIFACTS')) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
    $artifacts = Join-Path $repoRoot 'artifacts/automation-runner'
    $testProject = Join-Path $repoRoot 'OperationRecorderApp.Tests/OperationRecorderApp.Tests.csproj'
    $fixtureProject = Join-Path $repoRoot 'OperationRecorderApp.Tests/FixtureHost/OperationRecorder.RecordingFixture.csproj'
    $fixture = Join-Path $artifacts "bin/OperationRecorder.RecordingFixture/$($Configuration.ToLowerInvariant())/OperationRecorder.RecordingFixture.exe"
    if (-not $NoBuild) {
        if ((Invoke-Dotnet @('build', $testProject, '-c', $Configuration, '--artifacts-path', $artifacts, '-p:NuGetAudit=false') 'build.log') -ne 0) { throw 'アプリとテストのビルドに失敗しました。' }
        if ($Suite -ne 'Core' -and (Invoke-Dotnet @('build', $fixtureProject, '-c', $Configuration, '--artifacts-path', $artifacts, '-p:NuGetAudit=false') 'fixture-build.log') -ne 0) { throw '記録fixtureのビルドに失敗しました。' }
    }
    $targets = if ($Suite -eq 'All') { @('Core', 'Desktop') } else { @($Suite) }
    foreach ($target in $targets) { $summary.suites += Invoke-Suite $target }
    if (@($summary.suites | Where-Object { $_.status -eq 'failed' }).Count -gt 0) { $exitCode = 1; $summary.status = 'failed' }
    elseif (@($summary.suites | Where-Object { $_.status -eq 'blocked' }).Count -gt 0) { $exitCode = 3; $summary.status = 'blocked' }
    else { $exitCode = 0; $summary.status = 'passed' }
}
catch {
    $summary.reason = $_.Exception.Message
    Write-Host $_.Exception.Message -ForegroundColor Red
}
finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    $summary.exitCode = $exitCode; $summary.endedUtc = [DateTime]::UtcNow.ToString('o')
    if ($runRoot -and (Test-Path -LiteralPath $runRoot -PathType Container) -and $exitCode -ne 2) {
        $summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $runRoot 'summary.json') -Encoding utf8NoBOM
        Write-Host "結果: $(Join-Path $runRoot 'summary.json')"
    }
}
exit $exitCode
