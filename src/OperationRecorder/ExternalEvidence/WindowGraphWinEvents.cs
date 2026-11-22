using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace OperationRecorderApp.ExternalEvidence;

internal sealed record WindowGraphEventBoundary(long Count,
    IReadOnlyDictionary<(uint EventId, string Role), long> Totals);

internal sealed record WindowGraphEventCount(uint EventId, string Role, long Count);

internal sealed record WindowGraphEventInterval(string Status, long? TotalCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WindowGraphEventCount[] Counts = null)
{
    internal static WindowGraphEventInterval Between(WindowGraphRevisionSource first, WindowGraphRevisionSource next)
    {
        if (first == null || next == null || !ReferenceEquals(first.Epoch, next.Epoch) ||
            next.WinEventCount < first.WinEventCount)
            return new("unknown", null);

        long total = next.WinEventCount - first.WinEventCount;
        if (total == 0) return new("known", 0, []);
        var unavailable = new WindowGraphEventInterval("counts-unavailable", total);
        if (first.EventBoundary is not { } start || next.EventBoundary is not { } end ||
            start.Count != first.WinEventCount || end.Count != next.WinEventCount)
            return unavailable;

        var counts = new List<WindowGraphEventCount>();
        long remaining = total;
        foreach (var key in start.Totals.Keys.Union(end.Totals.Keys))
        {
            long count = end.Totals.GetValueOrDefault(key) - start.Totals.GetValueOrDefault(key);
            if (count < 0 || count > remaining) return unavailable;
            remaining -= count;
            if (count > 0) counts.Add(new(key.EventId, key.Role, count));
        }
        return remaining == 0
            ? new("known", total, counts.OrderBy(entry => entry.EventId)
                .ThenBy(entry => entry.Role, System.StringComparer.Ordinal).ToArray())
            : unavailable;
    }
}

/// <summary>Cumulative counts for the diagnostic registry. No individual events are retained.</summary>
internal sealed class WindowGraphWinEvents(Func<long, int, long> verifyWindowRoot)
{
    readonly object sync = new();
    readonly Dictionary<(uint EventId, string Role), long> totals = new();
    long count;
    long background;
    bool based, backgroundDestroyed;

    internal long Count { get { lock (sync) return count; } }

    internal WindowGraphEventBoundary Capture(long initialBackground)
    {
        lock (sync)
        {
            if (!based)
            {
                background = initialBackground;
                based = true;
            }
            return new(count, new Dictionary<(uint EventId, string Role), long>(totals));
        }
    }

    internal void Record(uint eventId, long target, long hwnd, int pid, bool destroyed)
    {
        lock (sync)
        {
            count++;
            if (destroyed && hwnd == background) backgroundDestroyed = true;
            long verifiedRoot = destroyed ? 0 : verifyWindowRoot(hwnd, pid);
            var key = (eventId, RoleOf(target, hwnd, verifiedRoot));
            totals[key] = totals.GetValueOrDefault(key) + 1;
        }
    }

    string RoleOf(long target, long hwnd, long verifiedRoot)
    {
        if (verifiedRoot == 0) return "unknown";
        if (verifiedRoot == target) return hwnd == target ? "target" : "target-child";
        if (background == 0) return "unknown";
        if (verifiedRoot != background) return "other";
        if (backgroundDestroyed) return "unknown";
        return hwnd == background ? "background" : "background-child";
    }
}
