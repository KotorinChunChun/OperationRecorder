using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Automation;

namespace OpRec
{
    public sealed partial class Recorder
    {
        readonly WindowChangeTracker windowChanges = new WindowChangeTracker();
        readonly Dictionary<IntPtr, WindowObservation> windowObservations = new Dictionary<IntPtr, WindowObservation>();
        IntPtr observedForeground;
        TaskbarClick taskbarClick;

        const string PopupMenuClass = "#32768";
        static bool IsTaskbarClass(string cls) => cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd";

        sealed class WindowObservation { public IntPtr Window, Previous; public long Ts; public string Kind; }
        sealed class TaskbarClick { public long Ts; public int X, Y; public bool Released; public string Label; }

        void InitializeWindowChanges(IntPtr? selected = null)
        {
            windowObservations.Clear(); taskbarClick = null;
            var windows = new List<long>();
            N.EnumWindows((h, l) => { if (N.IsWindowVisible(h)) windows.Add(h.ToInt64()); return true; }, IntPtr.Zero);
            observedForeground = selected ?? WinUtil.Root(N.GetForegroundWindow());
            windowChanges.Prime(windows, observedForeground.ToInt64());
        }

        void HoldForWindowChange(Ev ev)
        {
            if (Rule(CaptureRules.WindowChange).Record) ev.ObserveUntil = ev.Ts + WindowChangeTracker.AssociationMs + WindowChangeTracker.SettleMs;
        }

        bool RememberTaskbarClick(Raw r, IntPtr root)
        {
            if (!IsTaskbarClass(WinUtil.ClassOf(root))) return false;
            if (!Rule(CaptureRules.WindowChange).Record) return false;
            if (r.Button == RawButton.Left && Rule(CaptureRules.LeftClick).Record)
            {
                string label = null;
                try { label = AutomationElement.FromPoint(new System.Windows.Point(r.X, r.Y)).Current.Name; } catch { }
                if (label != null && label.Length > 80) label = label.Substring(0, 80) + "…";
                taskbarClick = new TaskbarClick { Ts = r.Ts, X = r.X, Y = r.Y, Label = label };
            }
            return true;
        }

        void ReleaseTaskbarClick(Raw r)
        {
            if (r.Button != RawButton.Left || taskbarClick == null) return;
            if (Math.Abs(r.X - taskbarClick.X) > 8 || Math.Abs(r.Y - taskbarClick.Y) > 8) taskbarClick = null;
            else taskbarClick.Released = true;
        }

        void OnWindowChange(Raw r)
        {
            if (r.Type == RawType.WindowDestroy)
            {
                windowChanges.Forget(r.Fg.ToInt64()); windowObservations.Remove(r.Fg); return;
            }
            IntPtr root = r.Fg;
            if (root == IntPtr.Zero || !N.IsWindow(root) || WinUtil.Root(root) != root) return;
            string cls = WinUtil.ClassOf(root);
            if (Mode == CaptureMode.App && TargetProcessName != null && !pids.Contains(WinUtil.PidOf(root))
                && WindowChangeTracker.ProcessMatches(TargetProcessName, WinUtil.ProcName(WinUtil.PidOf(root)))) RefreshPids();
            bool eligible = TargetReady.WaitOne(0) && N.IsWindowVisible(root) && !N.IsIconic(root)
                && TargetRules.IsUsableForeground(root, WinUtil.PidOf(root), OwnPid, cls)
                && cls != PopupMenuClass && (N.GetWindowLongPtr(root, N.GWL_EXSTYLE).ToInt64() & N.WS_EX_TOOLWINDOW) == 0 && InScope(root);
            IntPtr previous = observedForeground;
            string kind = windowChanges.Observe(root.ToInt64(), r.Type == RawType.WindowShow, r.Ts, eligible);
            if (r.Type == RawType.Foreground) observedForeground = root;
            if (kind == null) return;
            WindowObservation existing;
            if (windowObservations.TryGetValue(root, out existing) && existing.Kind == WindowChangeKinds.Open) return;
            windowObservations[root] = new WindowObservation { Window = root, Previous = previous, Ts = r.Ts, Kind = kind };
        }

        void FlushWindowChanges(long now, bool force)
        {
            foreach (var observation in windowObservations.Values.OrderBy(o => o.Ts).ToArray())
            {
                if (!force && now - observation.Ts < WindowChangeTracker.SettleMs) continue;
                windowObservations.Remove(observation.Window);
                var rule = Rule(CaptureRules.WindowChange);
                if (!rule.Record || !TargetReady.WaitOne(0) || !N.IsWindow(observation.Window)
                    || !N.IsWindowVisible(observation.Window) || !InScope(observation.Window)) continue;
                FinishCandidate(observation.Ts);
                var destination = WindowInfo(observation.Window);
                var change = new Dictionary<string, object> {
                    { "type", observation.Kind }, { "window", destination }, { "observedMs", observation.Ts },
                    { "relation", "observed" }, { "origin", "window" }
                };
                Ev cause;
                lock (pendingLock)
                {
                    cause = TaskbarClickExplains(observation.Ts) ? null : pending.Where(e => !e.Sent && !e.Rec.ContainsKey("windowEvent") && e.Root != IntPtr.Zero
                        && WindowChangeTracker.CanAssociate(e.Kind, e.UpTs > 0 ? e.UpTs : e.Ts, observation.Ts,
                            WinUtil.PidOf(e.Root) == WinUtil.PidOf(observation.Window), IsNavigationKey(e)))
                        .OrderByDescending(e => e.Ts).FirstOrDefault();
                    if (cause != null)
                    {
                        change["relation"] = "observedAfterInput";
                        change["origin"] = observation.Kind == WindowChangeKinds.Switch && (cause.Root == observation.Window || IsNavigationKey(cause)) ? "switch" : "action";
                        cause.Rec["windowEvent"] = change;
                        cause.ObserveUntil = 0;
                        if (cause.WantAfter && rule.After)
                        {
                            DropAfter(cause);
                            cause.AfterMissing = false; cause.AfterDue = observation.Ts + AfterMs;
                            cause.AfterRefTs = observation.Ts; cause.AfterRefName = "windowEvent";
                        }
                        TrySendLocked(cause);
                    }
                }
                if (cause != null) continue;
                var ev = NewEv(observation.Ts);
                ev.Kind = observation.Kind == WindowChangeKinds.Open ? EventKinds.WindowOpen : EventKinds.WindowSwitch;
                ev.Root = observation.Window; ev.WinFrame = WinUtil.GetFrame(observation.Window);
                ev.Rec["window"] = destination; ev.Rec["windowEvent"] = change;
                if (TaskbarClickExplains(observation.Ts))
                {
                    change["origin"] = "taskbar"; change["relation"] = "observedAfterInput";
                    change["label"] = taskbarClick.Label; taskbarClick = null;
                }
                ev.Before = rule.Before ? PickBefore(observation.Ts) : null;
                ScheduleAfter(ev, rule.After, observation.Ts);
                AddPendingAndSend(ev);
                RaiseCaptured(ev.Kind, null);
            }
            if (taskbarClick != null && now - taskbarClick.Ts > WindowChangeTracker.AssociationMs) taskbarClick = null;
            lock (pendingLock) foreach (var ev in pending.ToArray()) TrySendLocked(ev);
        }

        // タスクバーのボタンを押して離した直後の変化なら、その変化はタスクバーからの切り替え
        bool TaskbarClickExplains(long observedTs)
        {
            var click = taskbarClick;
            return click != null && click.Released && observedTs >= click.Ts
                && observedTs - click.Ts <= WindowChangeTracker.AssociationMs;
        }

        static bool IsNavigationKey(Ev ev)
        {
            object value;
            string key = ev.Rec.TryGetValue("key", out value) ? value as string : null;
            return ev.Kind == EventKinds.Key && WindowChangeTracker.IsNavigationKey(key);
        }
    }
}
