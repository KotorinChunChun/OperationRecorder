using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OperationRecorderApp.Replay;
using Xunit;

namespace OperationRecorderApp.Tests;

public class ReplayCoreTests
{
    public static ReplayConfig Config() => new()
    {
        StartDelayMs = 0, StableMs = 0, PollMs = 25, TimeoutMs = 100,
        Targets = new() { ["app"] = new() { Executable = Environment.ProcessPath, Title = "専用画面", RecordedWidth = 800, RecordedHeight = 600 } },
        Steps = new() { new() { Kind = "click", Target = "app", Element = new() { AutomationId = "stable", ControlType = "Button" } } },
    };
    [Fact]
    public void 記録を変えずに識別子と前後画像条件と元ハッシュを取り込む()
    {
        using var session = new TestSession(); var before = ReplayImporter.SourceHash(session.Dir);
        var config = ReplayImporter.Import(session.Dir);
        Assert.Equal(4, config.Steps.Count); Assert.Equal("btnRegister", config.Steps[0].Element.AutomationId);
        Assert.Equal("Enter", config.Steps[1].Key); Assert.Equal(-3, config.Steps[2].WheelNotches);
        Assert.NotNull(config.Steps[0].BeforeImage); Assert.NotNull(config.Steps[0].AfterImage);
        Assert.Equal(before, ReplayImporter.SourceHash(session.Dir)); Assert.Equal(before, config.SourceSha256);
        Assert.Contains(config.Validate(false), error => error.Contains("executable"));
    }
    [Fact]
    public void 記録が変更された場合と不明なconfig項目を拒否する()
    {
        using var session = new TestSession(); var config = ReplayImporter.Import(session.Dir);
        File.AppendAllText(Path.Combine(session.Dir, "events.jsonl"), "\n");
        Assert.Contains(config.Validate(), error => error.Contains("元の記録が変更"));
        var file = Path.Combine(session.Root, "bad.json"); File.WriteAllText(file, "{\"schemaVersion\":1,\"typo\":true}");
        Assert.Throws<JsonException>(() => ReplayConfig.Load(file));
        Assert.Throws<ArgumentException>(() => config.Save(Path.Combine(session.Root, "session.json")));
    }
    [Fact]
    public async Task ウィンドウとクラスと画像を監視してから一度だけ実行する()
    {
        var fake = new Runtime("waitingForWindow", "waitingForClass", "waitingForImage", "ready");
        var progress = new List<ReplayProgress>(); var engine = new ReplayEngine(fake, progress.Add);
        await engine.Run(Config(), default);
        Assert.Equal(1, fake.Executed); Assert.Equal(1, engine.CompletedSteps);
        Assert.Equal(new[] { "waitingForWindow", "waitingForClass", "waitingForImage", "executing", "completed" }, progress.Select(p => p.State));
    }
    [Fact]
    public async Task 一致しないままの上限とキャンセルで一切操作しない()
    {
        var fake = new Runtime("waitingForImage");
        await Assert.ThrowsAsync<TimeoutException>(() => new ReplayEngine(fake).Run(Config(), default));
        Assert.Equal(0, fake.Executed);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReplayEngine(new Runtime("ready")).Run(Config(), cancelled.Token));
    }
    [Fact]
    public async Task 時計が戻っても単調時計で待機上限を守る()
    {
        var runtime = new Runtime("waitingForImage") { WallClockBackwards = true };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<TimeoutException>(() => new ReplayEngine(runtime).Run(Config(), cancellation.Token));
        Assert.Equal(0, runtime.Executed);
    }
    [Theory]
    [InlineData("→", 39)]
    [InlineData("Ctrl+←", 37)]
    [InlineData("Num3", 99)]
    [InlineData("Ctrl+VK_0xBB", 187)]
    public void 既存記録の矢印記号とキーコードを再現できる(string key, int expected) => Assert.Equal(expected, ReplayKeys.Parse(key).Last());
    [Fact]
    public void 明示skipした未対応の手順だけなら未使用bindingの不足で妨げない()
    {
        var config = Config(); config.Targets["app"].Executable = ""; config.Steps[0].Skip = true; config.Steps[0].SkipReason = "記録の説明だけ";
        Assert.Empty(config.Validate());
        config.Steps[0].SkipReason = ""; Assert.NotEmpty(config.Validate());
    }
    [Fact]
    public async Task 後続手順が未確定なら最初のクリックも送らない()
    {
        var config = Config(); config.Steps.Add(new() { Kind = "input", Target = "app", Element = new() { AutomationId = "input" } });
        var fake = new Runtime("ready");
        await Assert.ThrowsAsync<ArgumentException>(() => new ReplayEngine(fake).Run(config, default)); Assert.Equal(0, fake.Executed);
        config.Steps[1].Text = new() { Literal = "秘密" }; config.Steps[1].IsPassword = true;
        Assert.Contains(config.Validate(), error => error.Contains("password"));
    }
    [Fact]
    public async Task 事後条件も監視し操作を自動で二重実行しない()
    {
        var config = Config(); config.Steps[0].AfterImage = new() { File = Path.GetTempFileName() };
        try
        {
            var fake = new Runtime("ready", "waitingForImage");
            await Assert.ThrowsAsync<TimeoutException>(() => new ReplayEngine(fake).Run(config, default)); Assert.Equal(1, fake.Executed);
        }
        finally { File.Delete(config.Steps[0].AfterImage.File); }
    }
    [Theory]
    [InlineData(-1000, 200, 1600, 900, .5, .5, -200, 650)]
    [InlineData(100, 50, 800, 600, 0, 1, 100, 649)]
    public void 負座標や寸法差を画面内の相対位置へ変換する(int x, int y, int w, int h, double rx, double ry, int expectedX, int expectedY) =>
        Assert.Equal(new Point(expectedX, expectedY), ReplayGeometry.Position(new(x, y, w, h), rx, ry));
    [Fact]
    public void 画像比較は縮尺を合わせて一致を検証し不正範囲を拒否する()
    {
        using var a = new Bitmap(100, 80); using var b = new Bitmap(200, 160);
        using (var g = Graphics.FromImage(a)) g.Clear(Color.Blue);
        using (var g = Graphics.FromImage(b)) g.Clear(Color.Blue);
        Assert.Equal(1, ReplayGeometry.Similarity(a, new(0, 0, 100, 80), b));
        using (var g = Graphics.FromImage(b)) g.Clear(Color.Red);
        Assert.True(ReplayGeometry.Similarity(a, new(0, 0, 100, 80), b) < .5);
        Assert.Throws<ArgumentException>(() => ReplayGeometry.Similarity(a, new(90, 0, 100, 80), b));
    }
    [Fact]
    public void 相対座標は縦横比とDPIの不一致を無条件に許可しない()
    {
        var target = new ReplayTarget { RecordedWidth = 800, RecordedHeight = 600, RecordedDpi = 96 };
        Assert.True(ReplayGeometry.Compatible(target, new(100, 50, 1600, 1200), 96));
        Assert.False(ReplayGeometry.Compatible(target, new(100, 50, 1600, 900), 96));
        Assert.False(ReplayGeometry.Compatible(target, new(100, 50, 1600, 1200), 144));
        target.AllowDpiChange = true;
        Assert.True(ReplayGeometry.Compatible(target, new(100, 50, 1600, 1200), 144));
    }
    [Theory]
    [InlineData("Ctrl+Alt+F20", true)]
    [InlineData("Enter", true)]
    [InlineData("Ctrl+Alt+Delete", false)]
    [InlineData("Win+L", false)]
    [InlineData("Ctrl+unknown", false)]
    [InlineData("Ctrl", false)]
    public void キーの再現可能性を入力前に確認する(string key, bool valid)
    {
        if (valid) Assert.NotEmpty(ReplayKeys.Parse(key)); else Assert.Throws<ArgumentException>(() => ReplayKeys.Parse(key));
    }
    [Fact]
    public void 開始条件は一回だけ発火し繰返しの遅刻分を蓄積しない()
    {
        var now = DateTimeOffset.Parse("2026-11-01T09:00:00+09:00"); var clock = new ReplayTriggerClock(now);
        Assert.True(clock.Due(0, new(), now)); Assert.False(clock.Due(0, new(), now));
        var interval = new ReplayTrigger { Type = "interval", IntervalSeconds = 60 };
        Assert.False(clock.Due(1, interval, now)); Assert.True(clock.Due(1, interval, now.AddMinutes(5))); Assert.False(clock.Due(1, interval, now.AddMinutes(5).AddSeconds(1)));
        var app = new ReplayTrigger { Type = "application" };
        Assert.True(clock.Due(2, app, now, applicationIdentity: "pid-a")); Assert.False(clock.Due(2, app, now, applicationIdentity: "pid-a")); Assert.True(clock.Due(2, app, now, applicationIdentity: "pid-b"));
    }
    [Fact]
    public void 日時のoffsetと遅刻幅と日次timezoneを検証する()
    {
        Assert.False(ReplayTriggerClock.TryOffsetTime("2026-11-01T09:00:00", out _));
        var now = DateTimeOffset.Parse("2026-11-01T00:00:00Z"); var clock = new ReplayTriggerClock(now);
        var at = new ReplayTrigger { Type = "at", At = "2026-11-01T09:00:00+09:00", LateGraceSeconds = 5 };
        Assert.False(clock.Due(0, at, now.AddSeconds(-1))); Assert.False(clock.Due(0, at, now.AddSeconds(6))); Assert.True(clock.Due(0, at, now));
        var daily = new ReplayTrigger { Type = "daily", Time = "09:00", TimeZone = "Tokyo Standard Time" };
        Assert.True(clock.Due(1, daily, now)); Assert.False(clock.Due(1, daily, now)); Assert.True(clock.Due(1, daily, now.AddDays(1)));
    }
    sealed class Runtime : IReplayRuntime
    {
        readonly Queue<string> states; string state = "ready";
        public int Executed;
        public bool WallClockBackwards;
        public Runtime(params string[] states) { this.states = new(states); }
        public DateTimeOffset Now { get; private set; } = DateTimeOffset.UtcNow;
        public long ElapsedMilliseconds { get; private set; }
        public Task Delay(int milliseconds, CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); Now = Now.AddMilliseconds(WallClockBackwards ? -milliseconds : milliseconds); ElapsedMilliseconds += milliseconds; return Task.CompletedTask; }
        public Task<ReplayObservation> Observe(ReplayConfig config, ReplayStep step, ReplayImage image, bool after, CancellationToken cancellation)
        { if (states.Count > 0) state = states.Dequeue(); return Task.FromResult(new ReplayObservation { Ready = state == "ready", State = state, Message = state }); }
        public Task Execute(ReplayConfig config, ReplayStep step, ReplayObservation observation, CancellationToken cancellation) { Executed++; return Task.CompletedTask; }
    }
}
