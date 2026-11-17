using System;
using System.Collections.Generic;
using System.Linq;

namespace OpRec
{
    // OS通知の重複を除く判断を、フックや画面から独立させる。利用は振り分けスレッドだけ。
    public sealed class WindowChangeTracker
    {
        public const int AssociationMs = 1000;
        public const int SettleMs = 200;
        readonly HashSet<long> known = new HashSet<long>();
        readonly Dictionary<long, long> shown = new Dictionary<long, long>();
        long foreground;

        public void Prime(IEnumerable<long> windows, long current)
        {
            known.Clear(); shown.Clear();
            foreach (long window in windows) known.Add(window);
            foreground = current;
        }

        public string Observe(long window, bool isShow, long time, bool eligible)
        {
            if (window == 0) return null;
            bool first = known.Add(window);
            if (isShow)
            {
                if (!first) return null;
                shown[window] = time;
                return eligible ? WindowChangeKinds.Open : null;
            }
            long previous = foreground; foreground = window;
            if (previous == window || !eligible) return null;
            long showTime;
            if (shown.TryGetValue(window, out showTime))
            {
                shown.Remove(window);
                if (time >= showTime && time - showTime <= 750) return null;
            }
            return first ? WindowChangeKinds.Open : WindowChangeKinds.Switch;
        }

        public void Forget(long window)
        {
            known.Remove(window); shown.Remove(window);
            if (foreground == window) foreground = 0;
        }

        public static bool CanAssociate(string kind, long actionTime, long observedTime, bool sameProcess, bool navigation)
        {
            if (observedTime < actionTime || observedTime - actionTime > AssociationMs) return false;
            if (!sameProcess && !navigation) return false;
            return kind == EventKinds.Click || kind == EventKinds.DoubleClick || kind == EventKinds.RightClick
                || kind == EventKinds.MiddleClick || kind == EventKinds.Key;
        }

        public static bool IsNavigationKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            var parts = key.Split('+');
            return (parts.Contains("Alt") && (parts.Contains("Tab") || parts.Contains("Esc")))
                || (parts.Contains("Win") && (parts.Contains("Tab") || (parts.Last().Length == 1 && char.IsDigit(parts.Last()[0]))));
        }

        public static bool ProcessMatches(string configured, string actual)
        {
            if (string.IsNullOrEmpty(configured)) return false;
            if (configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) configured = configured.Substring(0, configured.Length - 4);
            return string.Equals(configured, actual, StringComparison.OrdinalIgnoreCase);
        }
    }
}
