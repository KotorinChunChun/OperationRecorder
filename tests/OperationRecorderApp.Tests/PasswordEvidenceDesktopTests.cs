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
public sealed class PasswordEvidenceDesktopTests
{
    const int Width = 440;
    const int Height = 252;
    const string ProfileId = "fixture-password";
    const string SceneId = "fixture-known-password";
    const string StandardDummy = "OPREC_STANDARD_FAKE_PASSWORD";
    const string CustomDummy = "OPREC_CUSTOM_FAKE_PASSWORD";
    static readonly Rectangle StandardField = new(164, 74, 220, 28);
    static readonly Rectangle CustomField = new(164, 122, 220, 28);
    static readonly Rectangle StandardMask = Rectangle.Inflate(StandardField, 4, 4);
    static readonly Rectangle CustomMask = Rectangle.Inflate(CustomField, 4, 4);

    [Fact, Trait("Category", "Desktop")]
    public async Task 実WGCで標準と属性なしの欄を隠し移動後の証拠を確定する()
    {
        if (Environment.GetEnvironmentVariable("OPREC_PASSIVE_WINDOW_TESTS") != "1" || !WgcCaptureBackend.IsInputDesktopAvailable())
            throw new InvalidOperationException("DESKTOP_BLOCKED: 専用fixture指定と入力デスクトップへのアクセスが必要です。");

        string artifacts = Environment.GetEnvironmentVariable("OPREC_RECORDING_ARTIFACTS");
        string root = Path.Combine(artifacts ?? Path.GetTempPath(), "oprec-password-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var fixture = await Fixture.StartAsync();
            using var registry = new WindowGraphRegistry();
            var graph = await WaitForStableGraphAsync(registry, fixture.Target);
            var initial = graph.Windows.Single(window => window.Hwnd == fixture.Target.WindowHandle);
            Assert.Equal(new Size(Width, Height), initial.Bounds.Size);
            Assert.True(initial.Dpi > 0);
            var controls = (await fixture.CommandAsync("status")).Deserialize<FixtureStatus>(EvidenceProtocol.Json);
            var approvals = ReviewFixtureWindows(graph, fixture.Target, controls);

            string executable = PrepareProgram(Path.Combine(root, "program"));
            var selections = RegisterReviewedFixtureProfiles(Path.GetDirectoryName(executable), fixture.Target, initial.Dpi);

            var positive = await RunPowerShellAsync(root, executable, "positive", fixture, selections.Valid, approvals);
            Assert.Equal(1, positive.ActionCount);
            var first = ValidateCapture(positive.Before, fixture.Target, controls, "before", Color.Blue);
            var second = ValidateCapture(positive.After, fixture.Target, controls, "after", Color.Lime);
            Assert.Equal(first.X + 18, second.X);
            Assert.Equal(first.Y + 12, second.Y);
            Assert.Equal(initial.Dpi, first.Dpi);
            Assert.Equal(first.Dpi, second.Dpi);
            Assert.NotEqual(first.ImageSha256, second.ImageSha256);
            Assert.Equal(positive.Before.StepToken, positive.After.StepToken);
            Assert.NotEqual(Guid.Empty, positive.Before.StepToken);
            var moved = (await fixture.CommandAsync("status")).Deserialize<FixtureStatus>(EvidenceProtocol.Json);
            Assert.Equal(1, moved.ActionCount);
            Assert.Equal(controls.X + 18, moved.X);
            Assert.Equal(controls.Y + 12, moved.Y);
            await WaitForStableGraphAsync(registry, fixture.Target);

            var wrongDpi = await RunPowerShellAsync(root, executable, "wrong-dpi", fixture, selections.WrongDpi, approvals);
            Assert.Equal(0, wrongDpi.ActionCount);
            var dpiRejected = ValidateCapture(wrongDpi.Before, fixture.Target, controls, "before", null);
            Assert.Equal(initial.Dpi, dpiRejected.Dpi);

            var enlarged = (await fixture.CommandAsync("resize-field")).Deserialize<FixtureStatus>(EvidenceProtocol.Json);
            Assert.True(enlarged.CustomWidth > CustomMask.Right - CustomField.Left);
            Assert.Equal(controls.Custom, enlarged.Custom);
            await WaitForStableGraphAsync(registry, fixture.Target);
            var resized = await RunPowerShellAsync(root, executable, "changed-bounds", fixture, selections.Valid, approvals);
            Assert.Equal(0, resized.ActionCount);
            var rangeRejected = ValidateCapture(resized.Before, fixture.Target, controls, "before", null);

            await fixture.CommandAsync("reset-field");
            Assert.True((await fixture.CommandAsync("unknown")).GetProperty("unknown").GetBoolean());
            await WaitForStableGraphAsync(registry, fixture.Target);
            var unknown = await RunPowerShellAsync(root, executable, "unknown", fixture, selections.Valid, approvals);
            Assert.Equal(0, unknown.ActionCount);
            var unknownRejected = ValidateCapture(unknown.Before, fixture.Target, controls, "before", null);
            Assert.Equal(1, (await fixture.CommandAsync("status")).GetProperty("actionCount").GetInt32());

            File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
            {
                result = "passed",
                positive = new { first, second, actions = positive.ActionCount, standardPassword = true, customIsPassword = false },
                wrongDpi = new { expected = initial.Dpi + 24, actual = initial.Dpi, evidence = dpiRejected, actions = wrongDpi.ActionCount },
                changedBounds = new { evidence = rangeRejected, actions = resized.ActionCount },
                unknown = new { evidence = unknownRejected, actions = unknown.ActionCount },
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
        for (var ancestor = output; ancestor != null; ancestor = ancestor.Parent)
        {
            string source = Path.Combine(ancestor.FullName, "OperationRecorderApp", output.Name);
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

    static (PasswordProfileSelection Valid, PasswordProfileSelection WrongDpi) RegisterReviewedFixtureProfiles(
        string trustedDirectory, EvidenceTarget target, int dpi)
    {
        // Reviewed control topology of our dedicated Form; no pixels or values are enrolled.
        var layout = new EvidenceUi(new[]
        {
            new EvidenceNode(50032, 0, 0, Width, Height, false, true),
            new EvidenceNode(50020, 20, 74, 140, 28, false, true),
            new EvidenceNode(50004, StandardField.X, StandardField.Y, StandardField.Width, StandardField.Height, true, true),
            new EvidenceNode(50020, 20, 122, 140, 28, false, true),
            new EvidenceNode(50033, CustomField.X, CustomField.Y, CustomField.Width, CustomField.Height, false, true)
        }, false);
        string hash = PasswordEvidenceGate.StructureSha256(layout);
        Assert.NotNull(hash);
        var fields = new[]
        {
            new PasswordFieldRule("standard", "WindowsForms10.Static.", "専用標準欄:", "WindowsForms10.Edit.", 50004, StandardField, StandardMask),
            new PasswordFieldRule("custom", "WindowsForms10.Static.", "専用カスタム欄:", "WindowsForms10.Window.8.", 50033, CustomField, CustomMask)
        };
        var scene = new PasswordEvidenceScene(SceneId, new[] { "before", "after" }, dpi, Width, Height,
            "WindowsForms10.Window.8.", hash, fields);
        string assets = Path.Combine(trustedDirectory, "password-evidence-assets");
        Directory.CreateDirectory(assets);
        var valid = Register(1, scene);
        var wrongDpi = Register(2, scene with { Dpi = dpi + 24 });
        File.WriteAllBytes(Path.Combine(trustedDirectory, "password-evidence-catalog.json"), JsonSerializer.SerializeToUtf8Bytes(
            new { schemaVersion = 1, profiles = new[] { valid, wrongDpi } }, EvidenceProtocol.Json));
        Assert.Equal(ProfileId, PasswordEvidenceCatalog.Load(valid, trustedDirectory).ProfileId);
        return (valid, wrongDpi);

        PasswordProfileSelection Register(int revision, PasswordEvidenceScene approved)
        {
            var profile = new PasswordEvidenceProfile(1, ProfileId, revision, "synthetic-fixture", EvidenceFiles.HashFile(target.ExePath),
                FileVersionInfo.GetVersionInfo(target.ExePath).FileVersion, new[] { approved });
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(profile, EvidenceProtocol.Json);
            File.WriteAllBytes(Path.Combine(assets, FormattableString.Invariant($"{ProfileId}.{revision}.json")), bytes);
            return new(ProfileId, revision, EvidenceFiles.Hash(bytes));
        }
    }

    static async Task<PowerShellResult> RunPowerShellAsync(string root, string executable, string mode, Fixture fixture,
        PasswordProfileSelection selection, WindowApproval[] approvals)
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
            $profile = Test-OperationEvidencePasswordProfile -Executable $Executable -ProfileId $case.selection.profileId `
                -Revision $case.selection.revision -ProfileSha256 $case.selection.profileSha256 -TargetExecutable $case.target.exePath
            if ($profile.ProfileId -ne $case.selection.profileId -or $profile.Revision -ne $case.selection.revision) {
                throw '専用fixtureの承認資材検証に失敗しました。'
            }
            $session = New-OperationEvidenceSession -Executable $Executable -SchemaVersion 2 -Target $case.target `
                -GroupPolicy @{passwordProfile=$case.selection;approvals=$case.approvals;maxFrameAgeMs=9000} -OutputDirectory $OutputDirectory
            $actions = 0
            $after = $null
            try {
                $before = Send-OperationEvidenceRequest -Session $session -Request @{
                    method='BeginStep'; stepId=[guid]::NewGuid().ToString(); operationName='invoke'
                } -TimeoutMilliseconds 12000
                if ($before.status -eq 'before-saved' -and $before.operationAllowed -eq $true) {
                    if ($case.mode -ne 'positive') { throw '不明なfixtureへの操作許可を受け取りました。' }
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
            Assert.Contains("password-only-local-evidence", responses.Capabilities);
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

    static CaptureResult ValidateCapture(EvidenceReply reply, EvidenceTarget target, FixtureStatus controls, string phase, Color? marker)
    {
        bool allowed = marker.HasValue;
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
        Assert.Null(root.VerifiedFrameSha256);
        Assert.Null(root.ApprovedSceneId);

        if (allowed)
        {
            Assert.Equal("password-masked", reply.Quality.DisclosureStatus);
            Assert.Equal("password-masked", root.DisclosureStatus);
            Assert.Equal("wgc-window", root.CaptureMethod);
            Assert.Equal("fresh", root.CaptureStatus);
            Assert.True(root.FrameTime100ns > 0);
            Assert.NotNull(root.PasswordMask);
            Assert.Equal(SceneId, root.PasswordMask.SceneId);
            Assert.Equal(2, root.PasswordMask.Fields.Length);
            var standard = Assert.Single(root.PasswordMask.Fields, field => field.FieldId == "standard");
            Assert.Equal((controls.Standard, controls.StandardLabel), (standard.FieldHwnd, standard.LabelHwnd));
            var custom = Assert.Single(root.PasswordMask.Fields, field => field.FieldId == "custom");
            Assert.Equal((controls.Custom, controls.CustomLabel), (custom.FieldHwnd, custom.LabelHwnd));
            Assert.Equal(root.ImageSha256, EvidenceFiles.HashFile(Path.Combine(directory, root.ImageFile)));
            Assert.Equal(root.UiSha256, EvidenceFiles.HashFile(Path.Combine(directory, root.UiFile)));
            var ui = JsonSerializer.Deserialize<EvidenceUi>(File.ReadAllBytes(Path.Combine(directory, root.UiFile)), EvidenceProtocol.Json);
            Assert.False(ui.TextRead);
            Assert.Contains(ui.Elements, node => node.ControlTypeId == 50004 && node.X == StandardField.X && node.Y == StandardField.Y && node.IsPassword);
            Assert.Contains(ui.Elements, node => node.ControlTypeId == 50033 && node.X == CustomField.X && node.Y == CustomField.Y && !node.IsPassword);
        }
        else
        {
            Assert.Null(root.PasswordMask);
            Assert.Contains(reply.Quality.DisclosureStatus, new[] { "opaque-only", "blocked" });
        }

        foreach (var png in Directory.EnumerateFiles(directory, "*.png", SearchOption.AllDirectories))
        {
            using var file = File.OpenRead(png);
            using var image = new Bitmap(file);
            if (allowed)
            {
                Assert.Equal(new Size(Width, Height), image.Size);
                Assert.Equal(marker.Value.ToArgb(), image.GetPixel(24, 21).ToArgb());
                Assert.Equal(Color.White.ToArgb(), image.GetPixel(411, 164).ToArgb());
            }
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                {
                    Color pixel = image.GetPixel(x, y);
                    Assert.Equal(255, pixel.A);
                    if (!allowed || StandardMask.Contains(x, y) || CustomMask.Contains(x, y))
                        Assert.Equal(Color.Black.ToArgb(), pixel.ToArgb());
                    Assert.NotEqual(Color.Magenta.ToArgb(), pixel.ToArgb());
                }
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            byte[] bytes = File.ReadAllBytes(file);
            Assert.DoesNotContain(StandardDummy, Encoding.UTF8.GetString(bytes));
            Assert.DoesNotContain(CustomDummy, Encoding.UTF8.GetString(bytes));
            Assert.DoesNotContain(StandardDummy, Encoding.Unicode.GetString(bytes));
            Assert.DoesNotContain(CustomDummy, Encoding.Unicode.GetString(bytes));
        }
        return new(root.Window.Bounds.X, root.Window.Bounds.Y, root.Window.Dpi, root.CaptureStatus, root.DisclosureStatus,
            root.ReasonCode, commit.ImageSha256, root.PasswordMask?.SceneId);
    }

    static WindowApproval[] ReviewFixtureWindows(WindowGraphSnapshot graph, EvidenceTarget target, FixtureStatus controls)
    {
        var expectedChildren = new[] { controls.Standard, controls.StandardLabel, controls.Custom, controls.CustomLabel };
        Assert.Equal(expectedChildren.Order(), graph.Windows.Where(window => window.IsChild).Select(window => window.Hwnd).Order());
        return graph.Windows.Where(window => !window.IsChild).Select(window =>
        {
            Assert.Equal(target.Pid, window.Pid);
            if (window.Hwnd == target.WindowHandle) return new WindowApproval(window.Hwnd, "root");

            Assert.False(window.Visible);
            var className = new StringBuilder(256);
            Assert.True(GetClassName(new IntPtr(window.Hwnd), className, className.Capacity) > 0);
            string value = className.ToString();
            Assert.True(value is "IME" or "GDI+ Hook Window Class" ||
                value.StartsWith(".NET-BroadcastEventWindow.", StringComparison.Ordinal), "未知の専用fixture補助クラス: " + value);
            return new WindowApproval(window.Hwnd, "fixture-support", Required: false);
        }).ToArray();
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

    sealed record FixtureStatus(int ActionCount, int X, int Y, long Standard, long Custom, long StandardLabel, long CustomLabel,
        int CustomWidth, bool Unknown);
    sealed record PowerShellResult(int CallerPid, string[] Capabilities, int ActionCount, EvidenceReply Before, EvidenceReply After);
    sealed record CaptureResult(int X, int Y, int Dpi, string CaptureStatus, string DisclosureStatus, string ReasonCode, string ImageSha256, string SceneId);

    sealed class Fixture : IAsyncDisposable
    {
        readonly Process process;
        public string PipeName { get; }
        public EvidenceTarget Target { get; }

        Fixture(Process process, Guid id, EvidenceTarget target)
        {
            this.process = process;
            PipeName = "oprec-password-fixture-" + id.ToString("N");
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
            info.ArgumentList.Add("--password-evidence-window");
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
            return JsonSerializer.Deserialize<JsonElement>(await reader.ReadLineAsync(deadline.Token));
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
