param([Parameter(Mandatory)][string]$OutputDirectory, [string]$FixtureExe)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (-not $output.StartsWith((Join-Path $root 'dev/history/'), [StringComparison]::OrdinalIgnoreCase) -or $output -notmatch '[\\/]logs[\\/]') { throw '版別logsへ出力してください。' }
if (-not $FixtureExe) { $FixtureExe = Join-Path $root 'artifacts/automation-runner/bin/OperationRecorder.RecordingFixture/release/OperationRecorder.RecordingFixture.exe' }
$FixtureExe = (Resolve-Path -LiteralPath $FixtureExe).Path
if (@(Get-Process OperationRecorder -ErrorAction SilentlyContinue).Count) { throw '通常Recorderが起動中のため専用画面の採取を開始しません。' }
$desktop = Get-ChildItem 'C:/Program Files/dotnet/shared/Microsoft.WindowsDesktop.App' -Directory | Sort-Object Name -Descending | Select-Object -First 1
foreach ($file in @('WindowsBase.dll','UIAutomationTypes.dll','UIAutomationClient.dll','System.Drawing.Common.dll')) { [void][Reflection.Assembly]::LoadFrom((Join-Path $desktop.FullName $file)) }
[void][IO.Directory]::CreateDirectory($output)
foreach ($pass in 1..2) {
    $token = [Guid]::NewGuid().ToString('N'); $pipeName = 'OpRec.RecordingFixture.' + $token
    $start = [Diagnostics.ProcessStartInfo]::new($FixtureExe); $start.UseShellExecute = $false
    $start.ArgumentList.Add($pipeName); $start.ArgumentList.Add($token)
    $process = [Diagnostics.Process]::Start($start)
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(10000); $reader = [IO.StreamReader]::new($pipe)
        function Request-Fixture([string]$command) {
            $bytes = [Text.Encoding]::UTF8.GetBytes((@{token=$token; command=$command} | ConvertTo-Json -Compress) + "`n")
            $pipe.Write($bytes,0,$bytes.Length); $pipe.Flush()
            return ($reader.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult() | ConvertFrom-Json)
        }
        $state = Request-Fixture 'cover'
        if ($state.pid -ne $process.Id) { throw 'fixtureのPID不一致' }
        $window = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$state.hwnd)
        $bounds = $window.Current.BoundingRectangle
        $elements = @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition))
        $rows = foreach ($element in $elements) {
            $value = $element.Current; $rectangle = $value.BoundingRectangle
            [ordered]@{ automationId=$value.AutomationId; name=$value.Name; className=$value.ClassName; controlType=$value.ControlType.ProgrammaticName; isPassword=$value.IsPassword
                parent=[System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($element).Current.AutomationId
                bounds=@{ x=$rectangle.X-$bounds.X; y=$rectangle.Y-$bounds.Y; w=$rectangle.Width; h=$rectangle.Height }
                patterns=@($element.GetSupportedPatterns() | ForEach-Object ProgrammaticName) }
        }
        @{ pid=$process.Id; width=$bounds.Width; height=$bounds.Height; elements=$rows } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $output "uia-$pass.json") -Encoding utf8NoBOM
        $image = [Drawing.Bitmap]::new([int]$bounds.Width,[int]$bounds.Height)
        $graphics = [Drawing.Graphics]::FromImage($image)
        try { $graphics.CopyFromScreen([int]$bounds.X,[int]$bounds.Y,0,0,$image.Size); $image.Save((Join-Path $output "fixture-$pass.png")) } finally { $graphics.Dispose(); $image.Dispose() }
        [void](Request-Fixture 'close')
    }
    finally { $pipe.Dispose(); if (-not $process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }; $process.Dispose() }
}
'専用fixtureを2回起動し、UIAツリー・相対矩形・画面を採取しました。'
