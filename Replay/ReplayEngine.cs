using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OperationRecorderApp.Replay;

public sealed record ReplayProgress(int Step, string State, string Message);
public sealed class ReplayObservation
{
    public bool Ready { get; init; }
    public string State { get; init; } = "waitingForWindow";
    public string Message { get; init; } = "ウィンドウを探しています。";
    public object Context { get; init; }
}
public interface IReplayRuntime
{
    DateTimeOffset Now { get; }
    Task Delay(int milliseconds, CancellationToken cancellation);
    Task<ReplayObservation> Observe(ReplayConfig config, ReplayStep step, ReplayImage image, bool after, CancellationToken cancellation);
    Task Execute(ReplayConfig config, ReplayStep step, ReplayObservation observation, CancellationToken cancellation);
}
public sealed class ReplayEngine
{
    readonly IReplayRuntime runtime;
    readonly Action<ReplayProgress> progress;
    public int CompletedSteps { get; private set; }
    public ReplayEngine(IReplayRuntime runtime, Action<ReplayProgress> progress = null) { this.runtime = runtime; this.progress = progress ?? (_ => { }); }
    public async Task Run(ReplayConfig config, CancellationToken cancellation)
    {
        CompletedSteps = 0;
        var errors = config.Validate(); if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
        foreach (var step in config.Steps.Where(s => !s.Skip && s.Kind == "input" && !string.IsNullOrEmpty(s.Text?.Environment)))
            if (Environment.GetEnvironmentVariable(step.Text.Environment) == null) throw new ArgumentException("入力用の環境変数が未設定です（手順 " + step.SourceSeq + "）。");
        await runtime.Delay(config.StartDelayMs, cancellation);
        for (int index = 0; index < config.Steps.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested(); var step = config.Steps[index];
            if (step.Skip) { progress(new(index + 1, "skipped", "明示指定で除外しました。")); CompletedSteps++; continue; }
            await runtime.Delay((int)(Math.Min(step.DelayMs, config.MaxRecordedGapMs) / config.Speed), cancellation);
            var ready = step.Kind == "launch" ? new ReplayObservation { Ready = true } : await Wait(config, step, step.BeforeImage, index + 1, false, cancellation);
            progress(new(index + 1, "executing", "操作を実行しています。"));
            await runtime.Execute(config, step, ready, cancellation);
            if (step.AfterImage != null) await Wait(config, step, step.AfterImage, index + 1, true, cancellation);
            CompletedSteps++; progress(new(index + 1, "completed", "操作が完了しました。"));
        }
    }
    async Task<ReplayObservation> Wait(ReplayConfig config, ReplayStep step, ReplayImage image, int index, bool after, CancellationToken cancellation)
    {
        var started = runtime.Now; DateTimeOffset? stable = null; DateTimeOffset lastReport = DateTimeOffset.MinValue; string state = null;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var found = await runtime.Observe(config, step, image, after, cancellation);
            if (found.Ready)
            {
                stable ??= runtime.Now;
                if ((runtime.Now - stable.Value).TotalMilliseconds >= config.StableMs) return found;
            }
            else stable = null;
            if (found.State != state || (runtime.Now - lastReport).TotalSeconds >= 1)
            { progress(new(index, found.Ready ? "stabilizing" : found.State, found.Ready ? "一致状態が安定するのを待っています。" : found.Message)); state = found.State; lastReport = runtime.Now; }
            int timeout = step.TimeoutMs ?? config.TimeoutMs;
            if (timeout != 0 && (runtime.Now - started).TotalMilliseconds >= timeout) throw new TimeoutException("手順 " + index + ": " + found.Message + "（待機上限）");
            await runtime.Delay(config.PollMs, cancellation);
        }
    }
}
public static class ReplayGeometry
{
    public static bool IsUnitPoint(double x, double y) => double.IsFinite(x) && double.IsFinite(y) && x >= 0 && x <= 1 && y >= 0 && y <= 1;
    public static Point Position(Rectangle bounds, double x, double y)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || !IsUnitPoint(x, y)) throw new InvalidOperationException("操作位置を確定できません。");
        return new Point(bounds.X + (int)Math.Round(x * (bounds.Width - 1)), bounds.Y + (int)Math.Round(y * (bounds.Height - 1)));
    }
    public static bool Compatible(ReplayTarget target, Rectangle current, int dpi) => current.Width > 0 && current.Height > 0 &&
        (target.RecordedDpi == 0 || target.RecordedDpi == dpi || target.AllowDpiChange) &&
        target.RecordedWidth > 0 && target.RecordedHeight > 0 && Math.Abs((current.Width / (double)current.Height) / (target.RecordedWidth / (double)target.RecordedHeight) - 1) <= target.MaxAspectChange;
    public static double Similarity(Bitmap expected, Rectangle crop, Bitmap current)
    {
        if (crop.Width <= 0 || crop.Height <= 0 || !new Rectangle(Point.Empty, expected.Size).Contains(crop)) throw new ArgumentException("照合元の画像範囲が不正です。");
        double error = 0; const int size = 48;
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            var a = expected.GetPixel(crop.X + x * crop.Width / size, crop.Y + y * crop.Height / size);
            var b = current.GetPixel(x * current.Width / size, y * current.Height / size);
            error += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
        }
        return 1 - error / (size * size * 3 * 255d);
    }
}
