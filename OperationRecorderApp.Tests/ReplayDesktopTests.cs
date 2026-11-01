using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpRec;
using OperationRecorderApp.Replay;
using Xunit;

namespace OperationRecorderApp.Tests;

[Collection("管理画面の隔離設定")]
[Trait("Category", "Desktop")]
public class ReplayDesktopTests
{
    [Fact]
    public async Task 実記録からの再生は同じ要素と前後画像を確認してクリックする()
    {
        using var test = new RecordingDesktopTests.DesktopCase("replay-recorded-click");
        var recorder = test.Start(CaptureMode.Window);
        test.Input.Click(test.Fixture.Rect("action")); test.Wait(() => recorder.SavedCount == 1, "元の記録"); test.Stop();
        var config = ReplayImporter.Import(recorder.SessionDir); config.StartDelayMs = 0;
        Assert.True(config.Targets.Values.Single().RecordedDpi > 0);
        config.MaxRecordedGapMs = 0; config.TimeoutMs = 3000;
        string before = ReplayImporter.SourceHash(recorder.SessionDir);
        test.Fixture.Command("reset");
        var states = new List<ReplayProgress>();
        await new ReplayEngine(new WindowsReplayRuntime(config), states.Add).Run(config, default);
        Assert.Equal(1, test.Fixture.Command("status").GetProperty("actions").GetInt32());
        Assert.Equal(before, ReplayImporter.SourceHash(recorder.SessionDir));
        Assert.Contains(states, p => p.State == "completed"); test.Complete();
    }
    [Fact]
    public async Task ウィンドウと要素配置が変わってもUIAで再探索してクリックする()
    {
        using var test = new RecordingDesktopTests.DesktopCase("replay-shift-layout");
        var config = ForFixture(test);
        test.Fixture.Command("shift-layout");
        await new ReplayEngine(new WindowsReplayRuntime(config)).Run(config, default);
        test.Wait(() => test.Fixture.Command("status").GetProperty("actions").GetInt32() == 1, "再生したクリックの反映");
        Assert.Equal(1, test.Fixture.Command("status").GetProperty("actions").GetInt32()); test.Complete();
    }
    [Fact]
    public async Task 画像が一致するまで監視し変化後にだけ次へ進む()
    {
        using var test = new RecordingDesktopTests.DesktopCase("replay-image-wait");
        var recorder = test.Start(CaptureMode.Window);
        test.Input.Click(test.Fixture.Rect("action")); test.Wait(() => recorder.SavedCount == 1, "期待画像の保存"); test.Stop();
        var config = ReplayImporter.Import(recorder.SessionDir); config.StartDelayMs = 0; config.MaxRecordedGapMs = 0;
        config.TimeoutMs = 4000; config.Steps[0].Kind = "wait"; config.Steps[0].BeforeImage = config.Steps[0].AfterImage; config.Steps[0].AfterImage = null;
        test.Fixture.Command("reset");
        var states = new List<ReplayProgress>();
        bool changed = false;
        var engine = new ReplayEngine(new WindowsReplayRuntime(config), state =>
        {
            states.Add(state);
            if (state.State == "waitingForImage" && !changed) { changed = true; test.Fixture.Command("action"); }
        });
        await engine.Run(config, default);
        Assert.True(changed); Assert.Contains(states, p => p.State == "waitingForImage"); test.Complete();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 入力欄へ環境変数で補完しpasswordをconfigへ埋め込まない(bool password)
    {
        using var test = new RecordingDesktopTests.DesktopCase("replay-env-input");
        var config = ForFixture(test); var step = config.Steps[0]; step.Kind = "input";
        step.Element = new() { AutomationId = password ? "fixturePassword" : "fixtureInput", ControlType = "Edit" }; step.IsPassword = password;
        string name = "OPREC_REPLAY_TEST_" + Guid.NewGuid().ToString("N");
        step.Text = new() { Environment = name };
        var states = new List<ReplayProgress>();
        try
        {
            Environment.SetEnvironmentVariable(name, "fixture-value");
            await new ReplayEngine(new WindowsReplayRuntime(config), states.Add).Run(config, default);
            var state = test.Fixture.Command("status");
            if (password) Assert.Equal("fixture-value".Length, state.GetProperty("passwordLength").GetInt32());
            else Assert.Equal("fixture-value", state.GetProperty("value").GetString());
            Assert.DoesNotContain(states, p => p.Message.Contains("fixture-value")); test.Complete();
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }
    [Fact]
    public void ホットキー発火とbusy破棄と非常停止と解放を確認する()
    {
        using var test = new RecordingDesktopTests.DesktopCase("replay-hotkey");
        using var cancel = new CancellationTokenSource();
        using var keys = new ReplayHotkeys("Ctrl+Alt+Shift+F24", new[] { "Ctrl+Alt+Shift+F23" }, cancel);
        Assert.Null(keys.Error);
        test.Input.Chord(17, 18, 16, 0x86); test.Wait(() => keys.Consume("Ctrl+Alt+Shift+F23"), "開始トリガー");
        keys.Enabled = false; test.Input.Chord(17, 18, 16, 0x86); test.Delay(100); keys.Enabled = true;
        Assert.False(keys.Consume("Ctrl+Alt+Shift+F23"));
        test.Input.Chord(17, 18, 16, 0x87); test.Wait(() => cancel.IsCancellationRequested, "非常停止"); test.Complete();
    }
    internal static ReplayConfig ForFixture(RecordingDesktopTests.DesktopCase test)
    {
        using var p = Process.GetProcessById(test.Fixture.Pid);
        var config = ReplayCoreTests.Config(); config.TimeoutMs = 3000;
        config.Targets["app"].Executable = p.MainModule.FileName;
        config.Targets["app"].Title = test.Fixture.Command("status").GetProperty("title").GetString();
        config.Targets["app"].ProcessId = p.Id;
        config.Steps[0].Element = new() { AutomationId = "fixtureAction", ControlType = "Button" };
        return config;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 不一致のウィンドウやクラスは監視だけを続け入力せず停止する(bool wrongClass)
    {
        using var test = new RecordingDesktopTests.DesktopCase("replay-unmatched-" + wrongClass);
        var config = ForFixture(test); config.TimeoutMs = 100;
        if (wrongClass) config.Targets["app"].ClassPrefix = "存在しないクラス";
        else config.Targets["app"].Title = "存在しないタイトル";
        var progress = new List<ReplayProgress>();
        await Assert.ThrowsAsync<TimeoutException>(() => new ReplayEngine(new WindowsReplayRuntime(config), progress.Add).Run(config, default));
        Assert.Contains(progress, p => p.State == (wrongClass ? "waitingForClass" : "waitingForWindow"));
        Assert.Equal(0, test.Fixture.Command("status").GetProperty("actions").GetInt32()); test.Complete();
    }

    [Theory]
    [InlineData("application", 1)]
    [InlineData("interval", 2)]
    public void 実EXEのconfig監視でアプリ待ちと繰返しからクリックする(string trigger, int runs)
    {
        using var test = new RecordingDesktopTests.DesktopCase("replay-exe-" + trigger);
        using var scope = new AutomationScope(); var config = ForFixture(test);
        config.StopKey = "Ctrl+Alt+Shift+F24"; config.MaxRuns = runs; config.TriggerTimeoutMs = 5000;
        config.Triggers = new() { new() { Type = trigger, Target = "app", IntervalSeconds = 1 } };
        string file = scope.Workspace.Resolve("plan.json"); config.Save(file);
        var executed = scope.Process("--replay", "watch", "--config", file, "--logs", Path.Combine(test.Root, "cli-logs"));
        Assert.True(executed.Code == 0, executed.Output + "\n" + executed.Error); var response = System.Text.Json.Nodes.JsonNode.Parse(executed.Output);
        Assert.Equal(runs, response["runs"].GetValue<int>());
        Assert.Equal(runs, test.Fixture.Command("status").GetProperty("actions").GetInt32());
        Assert.True(File.Exists(Path.Combine(response["logDirectory"].GetValue<string>(), "progress.jsonl"))); test.Complete();
    }

    [Fact]
    public async Task ps1から実行しても状態表示をエラー扱いせずクリックを完了する()
    {
        using var test = new RecordingDesktopTests.DesktopCase("replay-ps1-run");
        using var scope = new AutomationScope(); var config = ForFixture(test); config.StopKey = "Ctrl+Alt+Shift+F24";
        string file = scope.Workspace.Resolve("plan.json"); config.Save(file);
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-File", Path.Combine(AppContext.BaseDirectory, "scripts", "Invoke-OperationReplay.ps1"),
            "-Config", file, "-Mode", "run", "-Executable", Path.Combine(AppContext.BaseDirectory, "OperationRecorder.exe"), "-LogDirectory", Path.Combine(test.Root, "ps1-logs") }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { process.Kill(true); throw; }
        var output = await stdout; await stderr;
        Assert.True(process.ExitCode == 0, output + "\n" + await stderr); Assert.Equal("completed", System.Text.Json.Nodes.JsonNode.Parse(output)["status"].GetValue<string>());
        test.Wait(() => test.Fixture.Command("status").GetProperty("actions").GetInt32() == 1, "ps1再生の反映");
        Assert.Equal(1, test.Fixture.Command("status").GetProperty("actions").GetInt32()); test.Complete();
    }

    [Fact]
    public async Task 記録した矢印キーを表示名ではなくキーコードで再現する()
    {
        using var test = new RecordingDesktopTests.DesktopCase("replay-recorded-key"); var recorder = test.Start(CaptureMode.Window);
        test.Input.Key(0x27); test.Wait(() => recorder.SavedCount == 1, "矢印キーの記録"); test.Stop();
        var config = ReplayImporter.Import(recorder.SessionDir); config.StartDelayMs = 0; config.MaxRecordedGapMs = 0;
        Assert.Equal("VK_0x27", config.Steps.Single().Key);
        config.Steps[0].BeforeImage = config.Steps[0].AfterImage = null;
        test.Fixture.Command("reset");
        await new ReplayEngine(new WindowsReplayRuntime(config)).Run(config, default);
        test.Wait(() => test.Fixture.Command("status").GetProperty("lastKey").GetInt32() == 39, "再生した矢印キーの反映");
        Assert.Equal(39, test.Fixture.Command("status").GetProperty("lastKey").GetInt32()); test.Complete();
    }
}
