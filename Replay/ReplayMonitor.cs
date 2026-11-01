using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OperationRecorderApp.Replay;

public sealed class ReplayMonitor
{
    readonly IReplayRuntime runtime;
    readonly Func<string, string> application;
    readonly Func<string, bool> consume;
    readonly Action<bool> enabled;
    readonly Action endRun;
    readonly Action<ReplayProgress> progress;
    public int Runs { get; private set; }
    public int CompletedSteps { get; private set; }
    public ReplayMonitor(IReplayRuntime runtime, Func<string, string> application, Func<string, bool> consume,
        Action<bool> enabled, Action endRun, Action<ReplayProgress> progress)
    { this.runtime = runtime; this.application = application; this.consume = consume; this.enabled = enabled; this.endRun = endRun; this.progress = progress; }
    public async Task Run(ReplayConfig config, bool watch, bool now, CancellationToken cancellation)
    {
        var errors = config.Validate(); if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
        if (!now && config.Triggers.All(t => t.Type == "at" && ReplayTriggerClock.TryOffsetTime(t.At, out var at) && runtime.Now > at.AddSeconds(t.LateGraceSeconds)))
            throw new ArgumentException("開始日時の遅刻許容幅を過ぎています。日時を見直してください。");
        var clock = new ReplayTriggerClock(runtime.Now); long waitingSince = runtime.ElapsedMilliseconds; long lastReport = long.MinValue / 2;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested(); bool due = now && Runs == 0;
            for (int i = 0; !due && i < config.Triggers.Count; i++)
            {
                var trigger = config.Triggers[i];
                due = clock.Due(i, trigger, runtime.Now, trigger.Type == "hotkey" && consume(trigger.Key),
                    trigger.Type == "application" ? application(trigger.Target) : null);
            }
            if (!due)
            {
                if (config.TriggerTimeoutMs > 0 && runtime.ElapsedMilliseconds - waitingSince >= config.TriggerTimeoutMs) throw new TimeoutException("開始条件の待機上限に達しました。");
                if (runtime.ElapsedMilliseconds - lastReport >= 1000)
                {
                    bool app = config.Triggers.Any(t => t.Type == "application");
                    progress(new(0, app ? "waitingForApplication" : "waitingForTrigger", app ? "アプリの起動を待っています。" : "開始トリガー/時刻を待っています。")); lastReport = runtime.ElapsedMilliseconds;
                }
                await runtime.Delay(config.PollMs, cancellation); continue;
            }
            enabled(false);
            var engine = new ReplayEngine(runtime, progress);
            try { await engine.Run(config, cancellation); }
            finally { CompletedSteps = engine.CompletedSteps; endRun(); enabled(true); }
            Runs++;
            if (!watch || config.MaxRuns > 0 && Runs >= config.MaxRuns) return;
            for (int i = 0; i < config.Triggers.Count; i++)
            {
                var trigger = config.Triggers[i]; clock.DiscardBusy(i, trigger, runtime.Now, trigger.Type == "application" ? application(trigger.Target) : null);
            }
            waitingSince = runtime.ElapsedMilliseconds; lastReport = long.MinValue / 2;
        }
    }
}
