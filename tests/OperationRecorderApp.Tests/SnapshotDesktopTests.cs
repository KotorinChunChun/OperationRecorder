using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Automation;
using OperationRecorderApp.ExternalEvidence;
using OpRec;
using Xunit;

namespace OperationRecorderApp.Tests;

[Collection("管理画面の隔離設定")]
public sealed class SnapshotDesktopTests
{
    const string DummyPassword = "OPREC_TEST_PASSWORD_0a17f68e";

    [Fact, Trait("Category", "Desktop")]
    public async Task 別ツールで親子窓と確認ダイアログを取得しても通常記録が続く()
    {
        if (Environment.GetEnvironmentVariable("OPREC_PASSIVE_WINDOW_TESTS") != "1" ||
            !WgcCaptureBackend.IsInputDesktopAvailable())
            throw new InvalidOperationException("DESKTOP_BLOCKED: 専用fixture指定と入力デスクトップへのアクセスが必要です。");

        using var fixture = new RecordingDesktopTests.FixtureProcess();
        using var fixtureProcess = Process.GetProcessById(fixture.Pid);
        var target = new EvidenceTarget(fixture.Pid, fixtureProcess.StartTime.ToUniversalTime(), fixtureProcess.MainModule.FileName,
            fixtureProcess.SessionId, fixture.Hwnd.ToInt64());
        fixture.Command("snapshot-paint");
        using var registry = new WindowGraphRegistry();
        await StableGraphAsync(registry, target, graph => graph.Windows.Any(window => window.Hwnd == target.WindowHandle));

        var artifacts = Environment.GetEnvironmentVariable("OPREC_RECORDING_ARTIFACTS");
        var root = Path.Combine(artifacts ?? Path.GetTempPath(), "oprec-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var rules = CaptureRules.Defaults();
        foreach (var rule in rules.Values) rule.Record = false;
        rules[CaptureRules.WindowChange] = new CaptureRule(true, false, false);
        var recorder = new Recorder { Mode = CaptureMode.App, TargetHwnd = fixture.Hwnd, Hotkeys = null, Rules = rules, IntervalMs = 50 };
        int beforeSnapshotCount = 0;
        int duringDialogCount = 0;
        try
        {
            recorder.Start(Path.Combine(root, "recording"));
            long child = fixture.Command("snapshot-child-open").GetProperty("snapshotChildHwnd").GetInt64();
            long owned = fixture.Command("window-open").GetProperty("secondaryHwnd").GetInt64();
            Assert.Equal(DummyPassword.Length, fixture.Command("snapshot-password-fill").GetProperty("passwordLength").GetInt32());
            fixture.Command("snapshot-paint");
            var before = await StableGraphAsync(registry, target,
                graph => graph.Windows.Any(window => window.Hwnd == child) && graph.Windows.Any(window => window.Hwnd == owned));
            var childRow = Assert.Single(before.Windows, window => window.Hwnd == child);
            Assert.True(childRow.IsChild);
            Assert.Equal(target.WindowHandle, childRow.ParentHwnd);
            Assert.Equal(target.WindowHandle, Assert.Single(before.Windows, window => window.Hwnd == owned).OwnerHwnd);
            await WaitAsync(() => recorder.SavedCount > 0, "所有ウィンドウの通常記録");
            beforeSnapshotCount = recorder.SavedCount;

            var first = await CaptureFromSeparateToolAsync(root, "with-owned", target, new(owned, "owned-fixture", false));
            var beforeCapture = ValidateSnapshot(first, target, child, owned);
            Assert.True(beforeCapture.Windows.First(window => window.Window.Hwnd == child).Window.Generation > 0);
            Assert.Equal(beforeSnapshotCount, recorder.SavedCount);

            fixture.Command("snapshot-dialog-open");
            var dialogGraph = await StableGraphAsync(registry, target,
                graph => graph.Windows.Any(window => IsOwnedSystemDialog(window, target)));
            long dialog = Assert.Single(dialogGraph.Windows, window => IsOwnedSystemDialog(window, target)).Hwnd;
            duringDialogCount = recorder.SavedCount;
            var second = await CaptureFromSeparateToolAsync(root, "with-dialog", target, new(dialog, "system-dialog", false));
            var dialogCapture = ValidateSnapshot(second, target, child, dialog);
            Assert.Equal(duringDialogCount, recorder.SavedCount);

            string dialogDismissal = await DismissDialogAsync(fixture, target, dialog);
            await WaitAsync(() => fixture.Command("status").GetProperty("snapshotDialogResult").GetString() == "OK", "ダイアログの確定");
            Assert.Equal(0, fixture.Command("snapshot-password-clear").GetProperty("passwordLength").GetInt32());
            fixture.Command("window-close");
            long nextOwned = fixture.Command("window-open").GetProperty("secondaryHwnd").GetInt64();
            string eventsPath = Path.Combine(recorder.SessionDir, "events.jsonl");
            await WaitAsync(() => RecordedEvents(eventsPath).Skip(duringDialogCount).Any(entry => Opened(entry, nextOwned)), "単発取得後の通常記録");

            recorder.Stop();
            var rows = RecordedEvents(eventsPath);
            Assert.True(rows.Length > duringDialogCount);
            Assert.All(rows, entry => Assert.Equal(fixture.Pid, entry.GetProperty("window").GetProperty("pid").GetInt32()));
            Assert.Contains(rows.Skip(duringDialogCount), entry => Opened(entry, nextOwned));
            File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
            {
                result = "passed", beforeSnapshotCount, duringDialogCount, finalRecordingCount = rows.Length,
                firstRootCaptureStatus = beforeCapture.Windows.First(window => window.Window.Hwnd == target.WindowHandle).CaptureStatus,
                secondRootCaptureStatus = dialogCapture.Windows.First(window => window.Window.Hwnd == target.WindowHandle).CaptureStatus,
                ownedCaptureStatus = beforeCapture.Windows.First(window => window.Window.Hwnd == owned).CaptureStatus, dialogDismissal,
                dialogCaptureStatus = dialogCapture.Windows.First(window => window.Window.Hwnd == dialog).CaptureStatus,
                verified = new[] { "child", "owned", "system-dialog", "password-protected", "separate-pwsh", "expired-request" }
            }, EvidenceProtocol.Json));
        }
        finally
        {
            recorder.Stop();
            if (artifacts == null) Directory.Delete(root, true);
        }
    }

    static async Task<JsonElement> CaptureFromSeparateToolAsync(string root, string caseName, EvidenceTarget target, WindowApproval window)
    {
        var run = Path.Combine(root, caseName);
        Directory.CreateDirectory(run);
        var output = Path.Combine(run, "external");
        var input = Path.Combine(run, "fixture.json");
        var script = Path.Combine(run, "capture.ps1");
        var policy = new GroupEvidencePolicy(Approvals: new[] { new WindowApproval(target.WindowHandle, "root"), window },
            MaxWindows: 32, MaxFrameAgeMs: 7000, MaxGroupSkewMs: 6000);
        File.WriteAllText(input, JsonSerializer.Serialize(new { target, policy }, EvidenceProtocol.Json));
        File.WriteAllText(script, """
            param($ModulePath, $Executable, $InputPath, $OutputDirectory)
            $ErrorActionPreference = 'Stop'
            Import-Module $ModulePath -Force
            $setup = Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json -AsHashtable -DateKind String
            $session = New-OperationEvidenceSession -Executable $Executable -SchemaVersion 2 -Target $setup.target `
                         -GroupPolicy $setup.policy -OutputDirectory $OutputDirectory
            try {
                $expired = Send-OperationEvidenceRequest -Session $session -Request @{
                    method = 'CaptureSnapshot'; deadlineUtc = [DateTimeOffset]::UtcNow.AddSeconds(-2).ToString('o')
                }
                $snapshot = Get-OperationEvidenceSnapshot -Session $session -TimeoutMilliseconds 12000
            } finally { $stopped = Close-OperationEvidenceSession -Session $session }
            @{ callerPid=$PID; snapshot=$snapshot; expired=$expired; stopped=$stopped } | ConvertTo-Json -Depth 12 -Compress
            """);
        var info = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-NoProfile", "-File", script, Path.Combine(AppContext.BaseDirectory, "scripts", "OperationRecorderEvidence.psm1"),
                     Path.Combine(AppContext.BaseDirectory, "OperationRecorder.exe"), input, output }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("検証用PowerShellクライアントを起動できません。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
            Assert.True(process.ExitCode == 0, await stderr);
            var json = await stdout;
            File.WriteAllText(Path.Combine(run, "responses.json"), json);
            var responses = JsonSerializer.Deserialize<JsonElement>(json);
            Assert.Equal(process.Id, responses.GetProperty("callerPid").GetInt32());
            Assert.NotEqual(Environment.ProcessId, process.Id);
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

    static GroupEvidenceManifest ValidateSnapshot(JsonElement responses, EvidenceTarget target, long child, long extra)
    {
        var response = responses.GetProperty("snapshot").Deserialize<EvidenceReply>(EvidenceProtocol.Json);
        var expired = responses.GetProperty("expired").Deserialize<EvidenceReply>(EvidenceProtocol.Json);
        var stopped = responses.GetProperty("stopped").Deserialize<EvidenceReply>(EvidenceProtocol.Json);
        Assert.Equal("invalid-request", expired.Status);
        Assert.False(expired.OperationAllowed);
        Assert.Empty(expired.EvidencePaths);
        Assert.Equal("stopped", stopped.Status);
        Assert.Empty(stopped.PendingSteps);
        Assert.Equal("snapshot-inadequate", response.Status);
        Assert.False(response.OperationAllowed);
        Assert.Equal(Guid.Empty, response.StepId);
        Assert.Equal(Guid.Empty, response.StepToken);
        Assert.Empty(response.PendingSteps);

        string commitPath = Assert.Single(response.EvidencePaths);
        string directory = Path.GetDirectoryName(commitPath);
        Assert.Equal(response.RequestId.ToString("N"), Path.GetFileName(directory));
        Assert.Equal("snapshots", Path.GetFileName(Path.GetDirectoryName(directory)));
        Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(directory)));
        var commit = JsonSerializer.Deserialize<EvidenceCommit>(File.ReadAllBytes(commitPath), EvidenceProtocol.Json);
        string groupPath = Path.Combine(directory, "group.json");
        Assert.Equal("snapshot", commit.Phase);
        Assert.Equal(response.RequestId, commit.StepId);
        Assert.Equal(Guid.Empty, commit.StepToken);
        Assert.Equal(EvidenceFiles.HashFile(groupPath), commit.GroupSha256);
        Assert.Equal(EvidenceFiles.HashFile(Path.Combine(directory, "frame.png")), commit.ImageSha256);
        Assert.Equal(EvidenceFiles.HashFile(Path.Combine(directory, "ui.json")), commit.UiSha256);
        var manifest = JsonSerializer.Deserialize<GroupEvidenceManifest>(File.ReadAllBytes(groupPath), EvidenceProtocol.Json);
        Assert.Equal("snapshot", manifest.Phase);
        Assert.Equal(manifest.CollectionEnded, response.CollectionEndedUtc);
        Assert.Equal(manifest.Windows.Any(row => row.CaptureStatus == "fresh") ? "captured" : "unavailable", response.Quality.CaptureStatus);
        Assert.Equal(target.WindowHandle, Assert.Single(manifest.Windows, row => row.Window.Hwnd == child).Window.ParentHwnd);
        Assert.Equal(target.WindowHandle, Assert.Single(manifest.Windows, row => row.Window.Hwnd == extra).Window.OwnerHwnd);
        var root = Assert.Single(manifest.Windows, row => row.Window.Hwnd == target.WindowHandle);
        var extraWindow = Assert.Single(manifest.Windows, row => row.Window.Hwnd == extra);
        foreach (var captured in new[] { root, extraWindow })
        {
            Assert.Equal("wgc-window", captured.CaptureMethod);
            Assert.Contains(captured.CaptureStatus, new[] { "fresh", "stale-frame" });
            Assert.True(captured.FrameTime100ns is > 0);
            Assert.NotNull(captured.ImageFile);
            Assert.NotNull(captured.UiFile);
            Assert.Equal("opaque-only", captured.DisclosureStatus);
            Assert.False(captured.RequirementsMet);
            if (captured.CaptureStatus == "stale-frame") Assert.Equal("state-unproven", captured.ReasonCode);
        }
        foreach (var row in manifest.Windows.Where(row => row.ImageFile != null))
        {
            Assert.Equal(row.ImageSha256, EvidenceFiles.HashFile(Path.Combine(directory, row.ImageFile)));
            Assert.Equal(row.UiSha256, EvidenceFiles.HashFile(Path.Combine(directory, row.UiFile)));
            var ui = JsonSerializer.Deserialize<EvidenceUi>(File.ReadAllBytes(Path.Combine(directory, row.UiFile)), EvidenceProtocol.Json);
            Assert.False(ui.TextRead);
            if (row.Window.Hwnd == target.WindowHandle)
                Assert.Contains(ui.Elements, element => element.IsPassword);
        }
        foreach (var png in Directory.EnumerateFiles(directory, "*.png", SearchOption.AllDirectories))
        {
            using var content = File.OpenRead(png);
            using var image = new Bitmap(content);
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                    Assert.Equal(Color.Black.ToArgb(), image.GetPixel(x, y).ToArgb());
        }
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            Assert.DoesNotContain(DummyPassword, Encoding.UTF8.GetString(bytes));
            Assert.DoesNotContain(DummyPassword, Encoding.Unicode.GetString(bytes));
        }
        return manifest;
    }

    static Task<string> DismissDialogAsync(RecordingDesktopTests.FixtureProcess fixture, EvidenceTarget target, long dialog)
    {
        const uint GwOwner = 4, WmCommand = 0x0111, SmtoAbortIfHung = 0x0002;
        const int IdOk = 1;
        var hwnd = new IntPtr(dialog);
        IntPtr ConfirmOkButton()
        {
            fixture.VerifyIdentity();
            GetWindowThreadProcessId(hwnd, out uint pid);
            Assert.Equal((uint)fixture.Pid, pid);
            Assert.Equal(target.WindowHandle, GetWindow(hwnd, GwOwner).ToInt64());
            Assert.Equal("#32770", WindowClass(hwnd));
            var button = GetDlgItem(hwnd, IdOk);
            Assert.NotEqual(IntPtr.Zero, button);
            GetWindowThreadProcessId(button, out pid);
            Assert.Equal((uint)fixture.Pid, pid);
            Assert.Equal("Button", WindowClass(button));
            return button;
        }

        return Task.Run(() =>
        {
            var ok = ConfirmOkButton();
            try
            {
                var button = AutomationElement.FromHandle(ok);
                Assert.Equal(fixture.Pid, button.Current.ProcessId);
                var invoke = Assert.IsType<InvokePattern>(button.GetCurrentPattern(InvokePattern.Pattern));
                invoke.Invoke();
                return "uia-invoke";
            }
            catch (InvalidOperationException)
            {
                // The system dialog provider can reject Invoke. Confirm the fixture before the
                // native notification. The caller checks DialogResult.OK.
                if (!IsWindow(hwnd)) return "uia-window-closed";
                Assert.Equal(ok, ConfirmOkButton());
                var delivered = SendMessageTimeout(hwnd, WmCommand, new IntPtr(IdOk), ok, SmtoAbortIfHung, 1000, out _);
                Assert.True(delivered != IntPtr.Zero || !IsWindow(hwnd), "専用確認ダイアログにIDOKを送れませんでした。");
                return "fixture-wm-command";
            }
        }).WaitAsync(TimeSpan.FromSeconds(5));
    }

    static async Task<WindowGraphSnapshot> StableGraphAsync(WindowGraphRegistry registry, EvidenceTarget target, Func<WindowGraphSnapshot, bool> ready)
    {
        WindowGraphSnapshot previous = null;
        var clock = Stopwatch.StartNew();
        long stableSince = 0;
        while (clock.ElapsedMilliseconds < 5000)
        {
            var graph = registry.Snapshot(target, 32);
            bool valid = !graph.Truncated && ready(graph);
            bool same = valid && previous != null && graph.Revision == previous.Revision;
            if (!same) stableSince = clock.ElapsedMilliseconds;
            if (same && clock.ElapsedMilliseconds - stableSince >= 250) return graph;
            previous = valid ? graph : null;
            await Task.Delay(50);
        }
        throw new TimeoutException("専用fixtureのウィンドウ構造が安定しませんでした。");
    }

    static async Task WaitAsync(Func<bool> ready, string phase)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 6000)
        {
            if (ready()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException(phase + "が確定しませんでした。");
    }

    static JsonElement[] RecordedEvents(string eventsPath)
    {
        using var stream = new FileStream(eventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        // The active recorder can be between writing a row and publishing its newline.
        var content = reader.ReadToEnd();
        int lastNewline = content.LastIndexOf('\n');
        if (lastNewline < 0) return Array.Empty<JsonElement>();
        return content[..lastNewline].Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
    }

    static bool Opened(JsonElement entry, long handle) => entry.GetProperty("kind").GetString() == "window-open" &&
        string.Equals(entry.GetProperty("window").GetProperty("hwnd").GetString(), $"0x{handle:X}", StringComparison.OrdinalIgnoreCase);

    static bool IsOwnedSystemDialog(WindowObservation window, EvidenceTarget target) => window.Visible &&
        window.Pid == target.Pid && window.OwnerHwnd == target.WindowHandle && WindowClass(new IntPtr(window.Hwnd)) == "#32770";

    static string WindowClass(IntPtr window)
    {
        var name = new StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr dialog, int itemId);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr window, uint message,
        IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
}
