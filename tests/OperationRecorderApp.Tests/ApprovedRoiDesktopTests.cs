using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

[Collection("管理画面の隔離設定")]
public sealed class ApprovedRoiDesktopTests
{
    const int Width = 360;
    const int Height = 230;
    const string ProfileId = "fixture-roi";
    const string SceneId = "fixture-known";
    const string DummyPassword = "ROI_FIXTURE_PASSWORD_NOT_A_CREDENTIAL";
    static readonly Rectangle Header = new(18, 22, 34, 15);
    static readonly Rectangle Action = new(26, 88, 34, 16);
    static readonly Rectangle Anchor = new(272, 18, 8, 8);

    [Fact, Trait("Category", "Desktop")]
    public async Task 実WGCと外部PowerShellで固定領域を確定し未知と認証画面を拒否する()
    {
        if (Environment.GetEnvironmentVariable("OPREC_PASSIVE_WINDOW_TESTS") != "1" || !WgcCaptureBackend.IsInputDesktopAvailable())
            throw new InvalidOperationException("DESKTOP_BLOCKED: 専用fixture指定と入力デスクトップへのアクセスが必要です。");

        string artifacts = Environment.GetEnvironmentVariable("OPREC_RECORDING_ARTIFACTS");
        string root = Path.Combine(artifacts ?? Path.GetTempPath(), "oprec-roi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var fixture = await Fixture.StartAsync();
            using var registry = new WindowGraphRegistry();
            var graph = await WaitForStableGraphAsync(registry, fixture.Target);
            var initialWindow = graph.Windows.Single(window => window.Hwnd == fixture.Target.WindowHandle);
            Assert.Equal(new Size(Width, Height), initialWindow.Bounds.Size);
            Assert.True(initialWindow.Dpi > 0);
            var approvals = graph.Windows.Select(window => ReviewFixtureWindow(window, fixture.Target)).ToArray();

            string executable = PrepareProgram(Path.Combine(root, "program"));
            var selection = RegisterReviewedFixtureProfile(Path.GetDirectoryName(executable), fixture.Target, initialWindow.Dpi);
            var positive = await RunPowerShellAsync(root, executable, "positive", fixture, selection, approvals);
            var first = ValidateCapture(positive.Before, fixture.Target, "before", true);
            Assert.Equal(1, positive.ActionCount);
            Assert.Equal(1, (await fixture.CommandAsync("status")).GetProperty("actionCount").GetInt32());
            var second = ValidateCapture(positive.After, fixture.Target, "after", true);
            Assert.Equal(first.ImageSha256, second.ImageSha256);

            Assert.True((await fixture.CommandAsync("unknown")).GetProperty("unknown").GetBoolean());
            await WaitForStableGraphAsync(registry, fixture.Target);
            var unknown = await RunPowerShellAsync(root, executable, "unknown", fixture, selection, approvals);
            Assert.Equal(0, unknown.ActionCount);
            var unknownResult = ValidateCapture(unknown.Before, fixture.Target, "before", false);
            Assert.Equal("roi-scene-unproven", unknownResult.ReasonCode);
            Assert.False(unknownResult.HasPassword);

            Assert.True((await fixture.CommandAsync("authentication")).GetProperty("authentication").GetBoolean());
            await WaitForStableGraphAsync(registry, fixture.Target);
            var authentication = await RunPowerShellAsync(root, executable, "authentication", fixture, selection, approvals);
            Assert.Equal(0, authentication.ActionCount);
            var authenticationResult = ValidateCapture(authentication.Before, fixture.Target, "before", false);
            Assert.Equal("roi-authentication-present", authenticationResult.ReasonCode);
            Assert.True(authenticationResult.HasPassword);
            Assert.Equal(1, (await fixture.CommandAsync("status")).GetProperty("actionCount").GetInt32());

            File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
            {
                result = "passed", selection, positive = new { first, second }, unknown = unknownResult,
                authentication = authenticationResult, actions = positive.ActionCount,
                verifiedBinaries = new[] { "OperationRecorder.exe", "OperationRecorder.dll", "scripts/OperationRecorderEvidence.psm1" }
                    .Select(file => new { file, sha256 = EvidenceFiles.HashFile(Path.Combine(Path.GetDirectoryName(executable), file)) })
            }, EvidenceProtocol.Json));
        }
        finally
        {
            if (artifacts == null) Directory.Delete(root, true);
        }
    }

    static string PrepareProgram(string destination)
    {
        Directory.CreateDirectory(destination);
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        string config = output.Name;
        for (var ancestor = output; ancestor != null; ancestor = ancestor.Parent)
        {
            string source = Path.Combine(ancestor.FullName, "OperationRecorderApp", config);
            if (!File.Exists(Path.Combine(source, "OperationRecorder.exe"))) continue;
            foreach (var file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            Directory.CreateDirectory(Path.Combine(destination, "scripts"));
            File.Copy(Path.Combine(source, "scripts", "OperationRecorderEvidence.psm1"),
                Path.Combine(destination, "scripts", "OperationRecorderEvidence.psm1"));
            return Path.Combine(destination, "OperationRecorder.exe");
        }
        throw new FileNotFoundException("OperationRecorderの開発ビルドがありません。");
    }

    static RoiProfileSelection RegisterReviewedFixtureProfile(string trustedDirectory, EvidenceTarget target, int dpi)
    {
        // The single borderless Form, its DPI/size and these solid drawings are reviewed fixture facts.
        // Nothing from a captured frame or a provider's live UI list becomes an approved asset.
        var layout = new EvidenceUi(new[] { new EvidenceNode(50032, 0, 0, Width, Height, false, true) }, false);
        var scene = new ApprovedRoiScene(SceneId, "root", new[] { "before", "after" }, dpi, Width, Height,
            ApprovedRoiGate.StructureSha256(layout), new[]
            {
                Region("header", Header, Color.Blue, true), Region("action", Action, Color.Lime, true),
                Region("anchor", Anchor, Color.Red, false)
            });
        var profile = new ApprovedRoiProfile(1, ProfileId, 1, EvidenceFiles.HashFile(target.ExePath), new[] { scene });
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(profile, EvidenceProtocol.Json);
        var selection = new RoiProfileSelection(ProfileId, 1, EvidenceFiles.Hash(bytes));
        string assets = Path.Combine(trustedDirectory, "approved-roi-assets");
        Directory.CreateDirectory(assets);
        File.WriteAllBytes(Path.Combine(assets, ProfileId + ".1.json"), bytes);
        File.WriteAllBytes(Path.Combine(trustedDirectory, "approved-roi-catalog.json"), JsonSerializer.SerializeToUtf8Bytes(
            new { schemaVersion = 1, profiles = new[] { selection } }, EvidenceProtocol.Json));
        Assert.Equal(ProfileId, ApprovedRoiCatalog.Load(selection, trustedDirectory).ProfileId);
        return selection;
    }

    static ApprovedRoiRegion Region(string id, Rectangle rectangle, Color color, bool publish)
    {
        var bgra = new byte[rectangle.Width * rectangle.Height * 4];
        for (int index = 0; index < bgra.Length; index += 4)
        {
            bgra[index] = color.B;
            bgra[index + 1] = color.G;
            bgra[index + 2] = color.R;
            bgra[index + 3] = 255;
        }
        return new(id, rectangle, bgra, publish);
    }

    static async Task<PowerShellResult> RunPowerShellAsync(string root, string executable, string mode, Fixture fixture,
        RoiProfileSelection selection, WindowApproval[] approvals)
    {
        string run = Path.Combine(root, mode);
        Directory.CreateDirectory(run);
        string config = Path.Combine(run, "fixture.json");
        string script = Path.Combine(run, "run.ps1");
        File.WriteAllBytes(config, JsonSerializer.SerializeToUtf8Bytes(new
        {
            target = fixture.Target, fixturePipe = fixture.PipeName, selection, mode, approvals
        }, EvidenceProtocol.Json));
        File.WriteAllText(script, """
            param($Module, $Executable, $Config, $OutputDirectory)
            $ErrorActionPreference = 'Stop'
            Import-Module $Module -Force
            $case = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json -AsHashtable -DateKind String
            $profile = Test-OperationEvidenceRoiProfile -Executable $Executable -ProfileId $case.selection.profileId `
                -Revision $case.selection.revision -ProfileSha256 $case.selection.profileSha256 -TargetExecutable $case.target.exePath
            if ($profile.ProfileId -ne $case.selection.profileId -or $profile.Revision -ne $case.selection.revision -or $profile.SceneCount -ne 1) {
                throw '専用fixtureの承認資材検証に失敗しました。'
            }
            $session = New-OperationEvidenceSession -Executable $Executable -SchemaVersion 2 -Target $case.target `
                -GroupPolicy @{roiProfile=$case.selection;approvals=$case.approvals;maxFrameAgeMs=9000} -OutputDirectory $OutputDirectory
            $actions = 0
            $after = $null
            try {
                $step = [guid]::NewGuid().ToString()
                $before = Send-OperationEvidenceRequest -Session $session -Request @{
                    method='BeginStep'; stepId=$step; operationName='invoke'
                } -TimeoutMilliseconds 12000
                if ($before.status -eq 'before-saved' -and $before.operationAllowed -eq $true) {
                    if ($case.mode -ne 'positive') { throw '不明・認証fixtureへの操作許可を受け取りました。' }
                    $control = [IO.Pipes.NamedPipeClientStream]::new('.', $case.fixturePipe, [IO.Pipes.PipeDirection]::InOut)
                    try {
                        $control.Connect(3000)
                        $writer = [IO.StreamWriter]::new($control)
                        $writer.AutoFlush = $true
                        $reader = [IO.StreamReader]::new($control)
                        $writer.WriteLine('advance')
                        $state = $reader.ReadLine() | ConvertFrom-Json
                        if ($state.actionCount -ne 1) { throw '専用fixtureの操作回数が一致しません。' }
                        $actions = 1
                    } finally { $control.Dispose() }
                    $after = Send-OperationEvidenceRequest -Session $session -Request @{
                        method='EndStep'; stepToken=$before.stepToken; outcome='success'
                    } -TimeoutMilliseconds 12000
                }
            } finally { $null = Close-OperationEvidenceSession -Session $session }
            @{ callerPid=$PID; capabilities=@($session.Capabilities); actionCount=$actions; before=$before; after=$after } | ConvertTo-Json -Depth 12 -Compress
            """);
        var info = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-File", script,
                     Path.Combine(Path.GetDirectoryName(executable), "scripts", "OperationRecorderEvidence.psm1"),
                     executable, config, Path.Combine(run, "output") })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("専用PowerShellクライアントの起動に失敗しました。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));
            Assert.True(process.ExitCode == 0, await stderr);
            string json = await stdout;
            File.WriteAllText(Path.Combine(run, "responses.json"), json);
            var responses = JsonSerializer.Deserialize<PowerShellResult>(json, EvidenceProtocol.Json);
            Assert.Equal(process.Id, responses.CallerPid);
            Assert.NotEqual(Environment.ProcessId, responses.CallerPid);
            Assert.Contains("approved-roi-scenes", responses.Capabilities);
            return responses;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    static CaptureResult ValidateCapture(EvidenceReply reply, EvidenceTarget target, string phase, bool allowed)
    {
        Assert.NotNull(reply);
        Assert.Equal(allowed ? phase + "-saved" : "before-inadequate", reply.Status);
        Assert.Equal(allowed && phase == "before", reply.OperationAllowed);
        Assert.Equal(allowed, reply.Quality.RequirementsMet);
        Assert.Equal(allowed ? "sufficient" : "insufficient", reply.Quality.DiagnosticStatus);
        string commitPath = Assert.Single(reply.EvidencePaths);
        string directory = Path.GetDirectoryName(commitPath);
        var commit = JsonSerializer.Deserialize<EvidenceCommit>(File.ReadAllBytes(commitPath), EvidenceProtocol.Json);
        string groupPath = Path.Combine(directory, "group.json");
        Assert.Equal(EvidenceFiles.HashFile(groupPath), commit.GroupSha256);
        Assert.Equal(EvidenceFiles.HashFile(Path.Combine(directory, "frame.png")), commit.ImageSha256);
        Assert.Equal(EvidenceFiles.HashFile(Path.Combine(directory, "ui.json")), commit.UiSha256);
        var group = JsonSerializer.Deserialize<GroupEvidenceManifest>(File.ReadAllBytes(groupPath), EvidenceProtocol.Json);
        Assert.Equal(phase, group.Phase);
        var root = Assert.Single(group.Windows, row => row.Window.Hwnd == target.WindowHandle);
        Assert.Equal("wgc-window", root.CaptureMethod);
        Assert.True(root.FrameTime100ns > 0);
        Assert.Equal("fresh", root.CaptureStatus);
        Assert.Equal(allowed ? SceneId : null, root.ApprovedSceneId);
        Assert.Null(root.VerifiedFrameSha256);
        string imagePath = Path.Combine(directory, root.ImageFile);
        string uiPath = Path.Combine(directory, root.UiFile);
        Assert.Equal(root.ImageSha256, EvidenceFiles.HashFile(imagePath));
        Assert.Equal(root.UiSha256, EvidenceFiles.HashFile(uiPath));
        var ui = JsonSerializer.Deserialize<EvidenceUi>(File.ReadAllBytes(uiPath), EvidenceProtocol.Json);
        Assert.False(ui.TextRead);

        foreach (var png in Directory.EnumerateFiles(directory, "*.png", SearchOption.AllDirectories))
        {
            using var file = File.OpenRead(png);
            using var image = new Bitmap(file);
            if (allowed)
            {
                Assert.Equal(new Size(Width, Height), image.Size);
                Assert.Equal(Color.Blue.ToArgb(), image.GetPixel(Header.Left, Header.Top).ToArgb());
                Assert.Equal(Color.Lime.ToArgb(), image.GetPixel(Action.Left, Action.Top).ToArgb());
            }
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                {
                    Color expected = allowed && Header.Contains(x, y) ? Color.Blue :
                        allowed && Action.Contains(x, y) ? Color.Lime : Color.Black;
                    Assert.Equal(expected.ToArgb(), image.GetPixel(x, y).ToArgb());
                }
        }
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            byte[] bytes = File.ReadAllBytes(file);
            foreach (var text in new[] { Encoding.UTF8.GetString(bytes), Encoding.Unicode.GetString(bytes) })
            {
                Assert.DoesNotContain(DummyPassword, text);
                Assert.DoesNotContain("C:/FakeData/", text);
                Assert.DoesNotContain("2031-02-03", text);
                Assert.DoesNotContain("2032-11-12", text);
            }
        }
        return new(root.CaptureStatus, root.DisclosureStatus, root.ReasonCode, root.ImageSha256,
            root.ApprovedSceneId, ui.Elements.Any(node => node.IsPassword));
    }

    static WindowApproval ReviewFixtureWindow(WindowObservation window, EvidenceTarget target)
    {
        Assert.Equal(target.Pid, window.Pid);
        if (window.Hwnd == target.WindowHandle) return new(window.Hwnd, "root");

        // The dedicated WinForms process creates these infrastructure windows at startup.
        // Bind only the confirmed, invisible handles from the initial scene. Never reuse the rule on new windows.
        Assert.False(window.IsChild);
        Assert.False(window.Visible);
        var className = new StringBuilder(256);
        Assert.True(GetClassName(new IntPtr(window.Hwnd), className, className.Capacity) > 0);
        string value = className.ToString();
        Assert.True(value is "IME" or "GDI+ Hook Window Class" ||
            value.StartsWith(".NET-BroadcastEventWindow.", StringComparison.Ordinal), "未知の専用fixture補助クラス: " + value);
        return new(window.Hwnd, "fixture-support", Required: false);
    }

    static async Task<WindowGraphSnapshot> WaitForStableGraphAsync(WindowGraphRegistry registry, EvidenceTarget target)
    {
        long? revision = null;
        var watch = Stopwatch.StartNew();
        long stableSince = 0;
        while (watch.ElapsedMilliseconds < 5000)
        {
            var graph = registry.Snapshot(target, 32);
            var root = graph.Windows.Single(window => window.Hwnd == target.WindowHandle);
            if (!graph.Truncated && root.Visible && revision == graph.Revision && watch.ElapsedMilliseconds - stableSince >= 250)
                return graph;
            if (revision != graph.Revision || graph.Truncated) stableSince = watch.ElapsedMilliseconds;
            revision = graph.Truncated ? null : graph.Revision;
            await Task.Delay(40);
        }
        throw new TimeoutException("専用fixtureのウィンドウ構造が安定しませんでした。");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr window, StringBuilder name, int count);

    sealed record PowerShellResult(int CallerPid, string[] Capabilities, int ActionCount, EvidenceReply Before, EvidenceReply After);
    sealed record CaptureResult(string CaptureStatus, string DisclosureStatus, string ReasonCode, string ImageSha256, string SceneId, bool HasPassword);

    sealed class Fixture : IAsyncDisposable
    {
        readonly Process process;
        public string PipeName { get; }
        public EvidenceTarget Target { get; }

        Fixture(Process process, Guid id, EvidenceTarget target)
        {
            this.process = process;
            PipeName = "oprec-roi-fixture-" + id.ToString("N");
            Target = target;
        }

        public static async Task<Fixture> StartAsync()
        {
            Guid id = Guid.NewGuid();
            string path = ExternalEvidenceProcessTests.FixtureExe;
            var info = new ProcessStartInfo(path)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            info.ArgumentList.Add("--approved-roi-window");
            info.ArgumentList.Add(id.ToString("N"));
            var process = Process.Start(info) ?? throw new InvalidOperationException("専用fixtureの起動に失敗しました。");
            try
            {
                string json = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                using var started = JsonDocument.Parse(json);
                Assert.Equal(process.Id, started.RootElement.GetProperty("pid").GetInt32());
                var target = new EvidenceTarget(process.Id, process.StartTime.ToUniversalTime(), process.MainModule.FileName,
                    process.SessionId, started.RootElement.GetProperty("hwnd").GetInt64());
                using var registry = new WindowGraphRegistry();
                Assert.True(registry.MatchesProcess(target));
                return new(process, id, target);
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.Dispose();
                throw;
            }
        }

        public async Task<JsonElement> CommandAsync(string command)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(deadline.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(command.AsMemory(), deadline.Token);
            string response = await reader.ReadLineAsync(deadline.Token);
            return JsonSerializer.Deserialize<JsonElement>(response);
        }

        public async ValueTask DisposeAsync()
        {
            using (process)
            {
                if (process.HasExited) return;
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
