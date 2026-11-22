using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OperationRecorderApp.Replay;
using Xunit;

namespace OperationRecorderApp.Tests;

[Collection("管理画面の隔離設定")]
[Trait("Category", "Desktop")]
[Trait("Depth", "ReplayRobustness")]
public sealed class ReplayRobustnessDesktopTests
{
    [Theory]
    [InlineData("startup", "waitingForWindow")]
    [InlineData("title", "waitingForWindow")]
    [InlineData("window-disabled", "waitingForWindow")]
    [InlineData("minimized", "waitingForWindow")]
    [InlineData("modal", "waitingForWindow")]
    [InlineData("control-disabled", "waitingForElement")]
    [InlineData("control-hidden", "waitingForElement")]
    [InlineData("control-offscreen", "waitingForElement")]
    public async Task 対象が出現して操作可能になるまで待ち一度だけクリックする(string scenario, string waiting)
    {
        using var test = new RecordingDesktopTests.DesktopCase("robust-" + scenario);
        var config = ReplayDesktopTests.ForFixture(test);
        config.TimeoutMs = 5000;
        config.StableMs = 100;
        test.Fixture.Command("replay-stage:" + scenario);
        var states = new List<string>();
        bool scheduled = false;
        var engine = new ReplayEngine(new WindowsReplayRuntime(config), progress =>
        {
            states.Add(progress.State);
            if (progress.State != waiting || scheduled) return;
            Assert.Equal(0, test.Fixture.Command("status").GetProperty("actions").GetInt32());
            scheduled = true;
            test.Fixture.Command("replay-ready-later");
        });

        await engine.Run(config, default);
        test.Wait(() => test.Fixture.Command("status").GetProperty("actions").GetInt32() == 1, "クリックの反映");
        var status = test.Fixture.Command("status");
        Assert.True(status.GetProperty("replayReady").GetBoolean());
        Assert.Equal(0, status.GetProperty("earlyActions").GetInt32());
        Assert.Equal(1, status.GetProperty("actions").GetInt32());
        Assert.Contains(waiting, states);
        Assert.Contains("stabilizing", states);
        Assert.Equal(1, engine.CompletedSteps);
        test.Complete();
    }

    [Theory]
    [InlineData("duplicate-window", "ウィンドウ候補が複数")]
    [InlineData("duplicate-control", "UI要素の候補が複数")]
    public async Task 曖昧な候補には入力を送らず停止する(string scenario, string reason)
    {
        using var test = new RecordingDesktopTests.DesktopCase("robust-" + scenario);
        var config = ReplayDesktopTests.ForFixture(test);
        if (scenario == "duplicate-control")
            config.Steps[0].Element = new() { Name = "記録対象の操作", ControlType = "Button" };
        test.Fixture.Command("replay-stage:" + scenario);
        var engine = new ReplayEngine(new WindowsReplayRuntime(config));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.Run(config, default));
        Assert.Contains(reason, error.Message);
        AssertStopped(test, engine);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 無効状態の待機上限とキャンセルでは入力を送らない(bool cancel)
    {
        using var test = new RecordingDesktopTests.DesktopCase("robust-wait-stop");
        var config = ReplayDesktopTests.ForFixture(test);
        config.TimeoutMs = 400;
        test.Fixture.Command("replay-stage:control-disabled");
        using var cancellation = new CancellationTokenSource();
        var engine = new ReplayEngine(new WindowsReplayRuntime(config), progress =>
        {
            if (cancel && progress.State == "waitingForElement") cancellation.Cancel();
        });

        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.Run(config, cancellation.Token));
        else
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(() => engine.Run(config, cancellation.Token));
            Assert.Contains("待機上限", error.Message);
        }
        AssertStopped(test, engine);
    }

    [Fact]
    public async Task 照合後に要素が無効化された場合も入力を送らない()
    {
        using var test = new RecordingDesktopTests.DesktopCase("robust-stale-element");
        var config = ReplayDesktopTests.ForFixture(test);
        var engine = new ReplayEngine(new WindowsReplayRuntime(config), progress =>
        {
            if (progress.State == "executing") test.Fixture.Command("replay-stage:control-disabled");
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.Run(config, default));
        Assert.Contains("実行直前", error.Message);
        AssertStopped(test, engine);
    }

    static void AssertStopped(RecordingDesktopTests.DesktopCase test, ReplayEngine engine)
    {
        Assert.Equal(0, engine.CompletedSteps);
        Assert.Equal(0, test.Fixture.Command("status").GetProperty("actions").GetInt32());
        test.Complete();
    }
}
