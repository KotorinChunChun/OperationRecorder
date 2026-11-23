using System;
using System.Collections.Generic;
using System.Linq;

namespace OpRec
{
    internal readonly record struct OperationTargetState(bool Enabled, bool Visible, bool Minimized, bool Foreground, bool Focused)
    {
        internal Dictionary<string, object> ToRecord() => new()
        {
            ["isEnabled"] = Enabled, ["isVisible"] = Visible, ["isMinimized"] = Minimized,
            ["isForeground"] = Foreground, ["hasFocus"] = Focused
        };
    }

    internal sealed record OperationTargetSample(IntPtr Root, IntPtr Handle, uint Pid, long ObservedMs, OperationTargetState State);
    internal sealed record OperationInputSample(OperationTargetSample Window, OperationTargetSample Control);

    internal sealed class OperationContextTracker
    {
        const int MaxTargets = 256;
        const int MaxChangesPerTarget = 8;

        sealed record Change(OperationTargetState From, OperationTargetState To, long ObservedMs)
        {
            internal Dictionary<string, object> ToRecord(long inputMs) => new()
            {
                ["observedMs"] = ObservedMs,
                ["elapsedToInputMs"] = inputMs - ObservedMs,
                ["from"] = From.ToRecord(),
                ["to"] = To.ToRecord()
            };
        }
        sealed record Latest(OperationTargetSample Sample, Change[] Changes);
        readonly Dictionary<IntPtr, Latest> observations = new();
        long? lastInputMs;

        internal void Forget(IntPtr hwnd)
        {
            observations.Remove(hwnd);
            foreach (var child in observations.Values.Where(v => v.Sample.Root == hwnd).Select(v => v.Sample.Handle).ToArray())
                observations.Remove(child);
        }

        internal void Observe(OperationTargetSample sample)
        {
            if (sample == null) return;
            Change[] changes = [];
            if (observations.TryGetValue(sample.Handle, out var last))
            {
                if (last.Sample.ObservedMs > sample.ObservedMs) return;
                if (SameTarget(last.Sample, sample))
                    changes = last.Sample.State == sample.State ? last.Changes :
                        [.. last.Changes.TakeLast(MaxChangesPerTarget - 1), new(last.Sample.State, sample.State, sample.ObservedMs)];
            }
            else if (observations.Count >= MaxTargets)
                observations.Remove(observations.MinBy(entry => entry.Value.Sample.ObservedMs).Key);
            observations[sample.Handle] = new(sample, changes);
        }

        static bool SameTarget(OperationTargetSample first, OperationTargetSample next) =>
            first.Root == next.Root && first.Handle == next.Handle && first.Pid == next.Pid;

        internal void MarkInput(long inputMs)
        {
            if (lastInputMs == null || inputMs >= lastInputMs) lastInputMs = inputMs;
        }

        internal Dictionary<string, object> RecordInput(long inputMs, OperationInputSample input, IntPtr expectedRoot, uint expectedPid)
        {
            var result = new Dictionary<string, object> { ["inputMs"] = inputMs };
            if (lastInputMs is long previous && previous <= inputMs) result["sincePreviousInputMs"] = inputMs - previous;
            MarkInput(inputMs);
            if (input?.Window is not { } root || root.Root != expectedRoot || root.Pid != expectedPid) return result;
            AddTargetRecord(result, "window", root, inputMs);
            AddTargetRecord(result, "control", input.Control, inputMs);
            return result;
        }

        void AddTargetRecord(Dictionary<string, object> result, string role, OperationTargetSample sample, long inputMs)
        {
            if (sample == null || sample.ObservedMs > inputMs) return;
            var state = sample.State.ToRecord();
            state["observedMs"] = sample.ObservedMs;
            result[role] = state;

            // 入力直前の観測を追加する前に、それまでの履歴と状態が一致するかを確かめる。
            if (observations.TryGetValue(sample.Handle, out var latest) && SameTarget(latest.Sample, sample) &&
                latest.Sample.ObservedMs <= inputMs && latest.Sample.State == sample.State && latest.Changes.Length > 0)
                result[role + "Changes"] = latest.Changes.Select(change => change.ToRecord(inputMs)).ToArray();
            Observe(sample);
        }
    }
}
