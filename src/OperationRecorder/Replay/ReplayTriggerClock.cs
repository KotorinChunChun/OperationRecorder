using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OperationRecorderApp.Replay;

// 実入力/OS予約に依存しない開始条件。busy中の過去分をまとめて追いかけない。
public sealed class ReplayTriggerClock
{
    readonly DateTimeOffset started;
    readonly Dictionary<int, string> fired = new();
    readonly Dictionary<int, DateTimeOffset> next = new();
    public ReplayTriggerClock(DateTimeOffset started) { this.started = started; }
    public static bool TryOffsetTime(string value, out DateTimeOffset time)
    {
        time = default;
        return value != null && (value.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(value, @"[+-]\d\d:\d\d$")) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }
    public bool Due(int index, ReplayTrigger trigger, DateTimeOffset now, bool hotkey = false, string applicationIdentity = null)
    {
        string token = null;
        switch (trigger.Type)
        {
            case "immediate": token = "once"; break;
            case "at":
                if (TryOffsetTime(trigger.At, out var at) && now >= at && now <= at.AddSeconds(trigger.LateGraceSeconds)) token = "once";
                break;
            case "interval":
                if (!next.TryGetValue(index, out var due)) next[index] = due = started.AddSeconds(trigger.IntervalSeconds);
                if (now >= due) { next[index] = now.AddSeconds(trigger.IntervalSeconds); return true; }
                break;
            case "daily":
                var zone = TimeZoneInfo.FindSystemTimeZoneById(trigger.TimeZone);
                var local = TimeZoneInfo.ConvertTime(now, zone);
                var planned = local.Date + TimeSpan.ParseExact(trigger.Time, "hh\\:mm", CultureInfo.InvariantCulture);
                if (local.DateTime >= planned && local.DateTime <= planned.AddSeconds(trigger.LateGraceSeconds)) token = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                break;
            case "hotkey": return hotkey;
            case "application": if (!string.IsNullOrEmpty(applicationIdentity)) token = applicationIdentity; break;
        }
        if (token == null || fired.TryGetValue(index, out var old) && old == token) return false;
        fired[index] = token; return true;
    }
    public void DiscardBusy(int index, ReplayTrigger trigger, DateTimeOffset now, string applicationIdentity = null)
    {
        if (trigger.Type == "interval") next[index] = now.AddSeconds(trigger.IntervalSeconds);
        else Due(index, trigger, now, applicationIdentity: applicationIdentity);
    }
}
