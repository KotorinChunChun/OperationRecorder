using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using OperationRecorderApp.Replay;
using Xunit;

namespace OperationRecorderApp.Tests;

public class ReplayCommandTests
{
    [Fact]
    public void 実EXEで元記録を変換し入力なしの確認と結果JSONを返す()
    {
        using var scope = new AutomationScope(); string session = scope.Session();
        string configFile = scope.Workspace.Resolve("replay.json");
        var imported = scope.Process("--replay", "import", "--source", session, "--out", configFile, "--no-images");
        Assert.Equal(0, imported.Code); Assert.Equal("draft", JsonNode.Parse(imported.Output)["status"].GetValue<string>());
        var config = ReplayConfig.Load(configFile);
        foreach (var target in config.Targets.Values) target.Executable = Environment.ProcessPath;
        // この既存出力用fixtureのホイールUIA矩形は疑似値なので、config側で明示補正する。
        config.Steps[2].X = config.Steps[2].Y = config.Steps[2].EndX = config.Steps[2].EndY = .5;
        config.Save(configFile);
        string result = scope.Workspace.Resolve("check-result.json");
        var check = scope.Process("--replay", "check", "--config", configFile, "--result", result);
        Assert.Equal(0, check.Code); Assert.Equal("ready", JsonNode.Parse(check.Output)["status"].GetValue<string>());
        Assert.Equal(JsonNode.Parse(check.Output).ToJsonString(), JsonNode.Parse(File.ReadAllText(result)).ToJsonString());
    }
    [Fact]
    public void 不正なconfigや同一結果保存先は入力や上書きの前に拒否する()
    {
        using var scope = new AutomationScope(); string file = scope.Workspace.Resolve("config.json");
        var config = ReplayCoreTests.Config(); config.Save(file); var before = File.ReadAllBytes(file);
        Assert.Equal(2, scope.Process("--replay", "run", "--config", file, "--result", file).Code);
        Assert.Equal(before, File.ReadAllBytes(file));
        config.Steps[0].Kind = "unknown"; config.Save(file);
        Assert.Equal(2, scope.Process("--replay", "run", "--config", file).Code);
        Assert.Equal(2, scope.Process("--replay", "run", "--config", file, "--source", "ignored").Code);
        Assert.Equal(2, scope.Process("--replay", "check", "--config").Code);
    }
    [Fact]
    public async Task ps1の確認入口も同じ終了コードとJSONを返す()
    {
        using var scope = new AutomationScope(); string file = scope.Workspace.Resolve("plan.json"); ReplayCoreTests.Config().Save(file);
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-File", Path.Combine(AppContext.BaseDirectory, "scripts", "Invoke-OperationReplay.ps1"), "-Config", file, "-Executable", Path.Combine(AppContext.BaseDirectory, "OperationRecorder.exe") }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw; }
        await stderr;
        Assert.Equal(0, process.ExitCode); Assert.Equal("ready", JsonNode.Parse(await stdout)["status"].GetValue<string>());
    }
    [Fact]
    public async Task interval監視は指定回数で終了しbusy中の発火を蓄積しない()
    {
        var config = ReplayCoreTests.Config(); config.MaxRuns = 2; config.Triggers = new() { new() { Type = "interval", IntervalSeconds = 1 } };
        var runtime = new Runtime { ActionMilliseconds = 3000 }; var progress = new System.Collections.Generic.List<ReplayProgress>();
        var monitor = Monitor(runtime, progress);
        await monitor.Run(config, true, false, default);
        Assert.Equal(2, runtime.Executed); Assert.Equal(2, monitor.Runs);
        Assert.True((runtime.Now - runtime.Started).TotalSeconds >= 8);
        Assert.Contains(progress, p => p.State == "waitingForTrigger");
    }
    [Fact]
    public async Task アプリ待機とキー待機と日時待機も監視ループから発火する()
    {
        foreach (var kind in new[] { "application", "hotkey", "at" })
        {
            var runtime = new Runtime(); var config = ReplayCoreTests.Config();
            config.Triggers = new() { new() { Type = kind, Target = "app", Key = "Ctrl+Alt+F20", At = runtime.Started.AddSeconds(1).ToString("O") } };
            var progress = new System.Collections.Generic.List<ReplayProgress>();
            await Monitor(runtime, progress).Run(config, false, false, default);
            Assert.Equal(1, runtime.Executed); Assert.Contains(progress, p => p.State.StartsWith("waiting"));
        }
    }
    [Fact]
    public async Task トリガー待機の上限と遅すぎる日時は操作しない()
    {
        var config = ReplayCoreTests.Config(); var runtime = new Runtime();
        config.TriggerTimeoutMs = 100; config.Triggers = new() { new() { Type = "hotkey", Key = "Ctrl+Alt+F20" } };
        await Assert.ThrowsAsync<TimeoutException>(() => Monitor(runtime, new()).Run(config, false, false, default));
        Assert.Equal(0, runtime.Executed);
        config.Triggers = new() { new() { Type = "at", At = runtime.Started.AddHours(-1).ToString("O") } };
        await Assert.ThrowsAsync<ArgumentException>(() => Monitor(runtime, new()).Run(config, false, false, default));
    }
    [Fact]
    public void 修飾キーの順番を変えても非常停止との重複を見逃さない()
    {
        var config = ReplayCoreTests.Config(); config.Triggers.Add(new() { Type = "hotkey", Key = "Alt+Ctrl+Pause" });
        Assert.Contains(config.Validate(), e => e.Contains("重複"));
    }
    static ReplayMonitor Monitor(Runtime runtime, System.Collections.Generic.List<ReplayProgress> progress) => new(runtime,
        _ => runtime.Ready ? "application" : "", _ => runtime.Ready, _ => { }, () => { }, progress.Add);
    sealed class Runtime : IReplayRuntime
    {
        public readonly DateTimeOffset Started = DateTimeOffset.UtcNow;
        public DateTimeOffset Now { get; private set; } = DateTimeOffset.UtcNow;
        public long ElapsedMilliseconds => (long)(Now - Started).TotalMilliseconds;
        public bool Ready => Now - Started >= TimeSpan.FromSeconds(1);
        public int ActionMilliseconds;
        public int Executed;
        public Task Delay(int milliseconds, CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); Now = Now.AddMilliseconds(milliseconds); return Task.CompletedTask; }
        public Task<ReplayObservation> Observe(ReplayConfig config, ReplayStep step, ReplayImage image, bool after, CancellationToken cancellation) => Task.FromResult(new ReplayObservation { Ready = true });
        public Task Execute(ReplayConfig config, ReplayStep step, ReplayObservation observation, CancellationToken cancellation) { Executed++; Now = Now.AddMilliseconds(ActionMilliseconds); return Task.CompletedTask; }
    }
}
