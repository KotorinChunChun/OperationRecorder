using System;
using System.Drawing;
using System.Windows.Automation;

namespace OpRec
{
    // マウス: クリック・ダブルクリック・ドラッグ・ホイール
    public sealed partial class Recorder
    {
        readonly Ev[] openDown = new Ev[3];   // ボタン(RawButton)ごとの、押したまま離していない操作
        Ev lastClick;
        bool swallowPickUp;

        // 同じウィンドウで続けて回したもの(前の回転から 1 秒以内)は 1 つの手順にまとめ、操作後は最後に回した後の画面にする。
        // 上下に回した分は差し引き、元の位置に戻した(合計 0)ときは記録しない
        Ev lastWheel;

        void OnMouseDown(Raw r)
        {
            composing = false;
            IntPtr hwnd = N.WindowFromPoint(new N.POINT { X = r.X, Y = r.Y });
            IntPtr root = WinUtil.Root(hwnd);

            taskbarClick = null;
            if (TargetReady.WaitOne(0) && RememberTaskbarClick(r, root)) return;

            if (!TargetReady.WaitOne(0))
            {
                PickTargetByClick(r, root);
                return;
            }

            if (!InScope(root, new Point(r.X, r.Y)))
            {
                openDown[r.Button] = null;
                lastClick = null;
                return;
            }

            // 撮影ルールで記録しない操作は、ここで捨てる
            var mrule = Rule(r.Button == RawButton.Left ? CaptureRules.LeftClick : r.Button == RawButton.Right ? CaptureRules.RightClick : CaptureRules.MiddleClick);
            if (!mrule.Record)
            {
                openDown[r.Button] = null;
                if (r.Button == RawButton.Left) lastClick = null;
                return;
            }

            if (TryMergeDoubleClick(r, root)) return;

            var ev = NewEv(r.Ts);
            ev.IsMouse = true;
            ev.Button = r.Button;
            ev.Kind = EventKinds.ClickOf(r.Button);
            ev.X = ev.EndX = r.X;
            ev.Y = ev.EndY = r.Y;
            ev.Root = root;
            HoldForWindowChange(ev);
            ev.Before = mrule.Before ? PickBefore(r.Ts) : null;
            ev.WantAfter = mrule.After;   // 操作後の撮影は、ボタンを離したときに予約する
            lock (pendingLock) pending.Add(ev);
            openDown[r.Button] = ev;
            lastClick = r.Button == RawButton.Left ? ev : null;
            RaiseCaptured(ev.Kind, new Point(r.X, r.Y));

            try { FillMouseInfo(ev, hwnd, root); } catch (Exception ex) { Log("要素情報の取得に失敗: " + ex.Message); }
            CompleteInfo(ev);
        }

        // 「最初にクリックしたもの」を対象にする記録で、まだ対象が決まっていないときのクリック
        void PickTargetByClick(Raw r, IntPtr root)
        {
            if (!PickTarget || r.Button != RawButton.Left) return;
            if (Mode == CaptureMode.Desktop)
            {
                // Desktop モードの Pick は、クリックしたウィンドウが映っているディスプレイを対象にする
                TargetMonitor = Monitors.OfWindow(root);
                if (TargetMonitor == null) return;
                Log("対象を選択しました: ディスプレイ " + TargetMonitor.Index + " " + TargetMonitor.Describe());
            }
            else
            {
                TargetHwnd = root;
                if (Mode == CaptureMode.App) RefreshPids();
                Log("対象を選択しました: " + WinUtil.Describe(root));
            }
            WriteSession(false);
            swallowPickUp = true;
            TargetReady.Set();
            InitializeWindowChanges(root);
        }

        // 直前のクリックと同じ場所・ダブルクリックの間隔内なら、そのクリックをダブルクリックに変える
        bool TryMergeDoubleClick(Raw r, IntPtr root)
        {
            lock (pendingLock)
            {
                var lc = lastClick;
                int dcx = N.GetSystemMetrics(N.SM_CXDOUBLECLK), dcy = N.GetSystemMetrics(N.SM_CYDOUBLECLK);
                if (lc == null || r.Button != RawButton.Left || lc.Sent || lc.Kind != EventKinds.Click || lc.Root != root
                    || r.Ts - lc.Ts > N.GetDoubleClickTime()
                    || Math.Abs(r.X - lc.X) > dcx || Math.Abs(r.Y - lc.Y) > dcy) return false;

                lc.Kind = EventKinds.DoubleClick;
                DropAfter(lc);
                lc.AfterDue = long.MaxValue;
                openDown[RawButton.Left] = lc;
                lastClick = null;
                return true;
            }
        }

        void OnMouseUp(Raw r)
        {
            ReleaseTaskbarClick(r);
            if (swallowPickUp && r.Button == RawButton.Left) { swallowPickUp = false; return; }
            var ev = openDown[r.Button];
            openDown[r.Button] = null;
            if (ev == null) return;
            if (Rule(CaptureRules.WindowChange).Record) ev.ObserveUntil = r.Ts + WindowChangeTracker.AssociationMs + WindowChangeTracker.SettleMs;
            lock (pendingLock)
            {
                if (ev.Sent) return;
                int tx = Math.Max(8, N.GetSystemMetrics(N.SM_CXDRAG) * 2), ty = Math.Max(8, N.GetSystemMetrics(N.SM_CYDRAG) * 2);
                if (ev.Kind != EventKinds.DoubleClick && (Math.Abs(r.X - ev.X) > tx || Math.Abs(r.Y - ev.Y) > ty))
                {
                    ev.Kind = EventKinds.DragOf(ev.Button);
                    ev.EndX = r.X;
                    ev.EndY = r.Y;
                    if (lastClick == ev) lastClick = null;

                    // ウィンドウを別のディスプレイへ(から)ドラッグで移動しただけの操作は、手順ではないので記録しない。
                    // 押した位置と離した位置のディスプレイが違い、ウィンドウ自体の位置が変わっていれば「ウィンドウの移動」とみなす
                    if (IsCrossMonitorWindowMove(ev, r))
                    {
                        pending.Remove(ev);
                        ReleaseFrames(ev);
                        Log("記録しません: ウィンドウを別のディスプレイへ移動するドラッグ");
                        return;
                    }
                }
                ev.UpTs = r.Ts;
                DropAfter(ev);
                if (ev.WantAfter) ev.AfterDue = r.Ts + AfterMs;
                else { ev.AfterMissing = true; TrySendLocked(ev); }   // 「操作後を撮る」が切られている
            }
        }

        void OnWheel(Raw r)
        {
            if (!TargetReady.WaitOne(0) || r.Vk == 0) return;
            var rule = Rule(CaptureRules.MouseWheel);
            if (!rule.Record) return;
            IntPtr hwnd = N.WindowFromPoint(new N.POINT { X = r.X, Y = r.Y });
            IntPtr root = WinUtil.Root(hwnd);
            if (!InScope(root, new Point(r.X, r.Y))) return;
            int delta = WheelRules.Notches(r.Vk);

            lock (pendingLock)
            {
                var w = lastWheel;
                // 向きが逆でもまとめて差し引く(下に回して上に戻したら相殺され、0 なら記録しない)
                if (w != null && WheelRules.CanMerge(w.Sent, w.Root, w.WheelLastTs, root, r.Ts))
                {
                    w.WheelNet += delta;
                    w.WheelLastTs = r.Ts;
                    w.Rec["wheel"] = WheelRules.Info(w.WheelNet);
                    DropAfter(w);
                    if (w.WantAfter) w.AfterDue = r.Ts + AfterMs;
                    return;
                }
            }

            var ev = NewEv(r.Ts);
            ev.IsMouse = true;
            ev.Kind = EventKinds.Wheel;
            ev.X = ev.EndX = r.X;
            ev.Y = ev.EndY = r.Y;
            ev.Root = root;
            ev.WheelNet = delta;
            ev.WheelLastTs = r.Ts;
            ev.Before = rule.Before ? PickBefore(r.Ts) : null;
            ScheduleAfter(ev, rule.After, r.Ts);
            ev.Rec["wheel"] = WheelRules.Info(ev.WheelNet);
            lock (pendingLock) { pending.Add(ev); lastWheel = ev; }
            RaiseCaptured(EventKinds.Wheel, new Point(r.X, r.Y));
            try { FillMouseInfo(ev, hwnd, root); } catch (Exception ex) { Log("要素情報の取得に失敗: " + ex.Message); }
            CompleteInfo(ev);
        }

        static bool IsCrossMonitorWindowMove(Ev ev, Raw up)
        {
            IntPtr m1 = N.MonitorFromPoint(new N.POINT { X = ev.X, Y = ev.Y }, N.MONITOR_DEFAULTTONEAREST);
            IntPtr m2 = N.MonitorFromPoint(new N.POINT { X = up.X, Y = up.Y }, N.MONITOR_DEFAULTTONEAREST);
            if (m1 == m2 || ev.Root == IntPtr.Zero || !N.IsWindow(ev.Root)) return false;
            Rectangle now = WinUtil.GetFrame(ev.Root);
            return now.Location != ev.WinFrame.Location || now.Size != ev.WinFrame.Size;
        }

        void FillMouseInfo(Ev ev, IntPtr hwnd, IntPtr root)
        {
            Rectangle rf = WinUtil.GetFrame(root);
            ev.WinFrame = rf;
            ev.Rec["window"] = WindowInfo(root);
            ev.Rec["pointInWindow"] = WinUtil.Pt(ev.X - rf.X, ev.Y - rf.Y);
            var cp = new N.POINT { X = ev.X, Y = ev.Y };
            N.ScreenToClient(root, ref cp);
            ev.Rec["pointInClient"] = WinUtil.Pt(cp.X, cp.Y);
            ev.Rec["win32"] = Win32Info(hwnd, root);

            AutomationElement el = null;
            try { el = AutomationElement.FromPoint(new System.Windows.Point(ev.X, ev.Y)); } catch { }
            if (el != null) ev.Rec["uia"] = UiaInfo(el, root, rf, ev);
        }
    }
}
