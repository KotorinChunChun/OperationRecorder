using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace OpRec
{
    public sealed partial class Recorder
    {
        uint hookTid;
        IntPtr mouseHook, keyHook;
        N.HookProc mouseDel, keyDel;   // GC に回収されないようフィールドで保持する
        N.WinEventProc focusDel;
        IntPtr focusHook;
        readonly HashSet<int> keysDown = new HashSet<int>();
        bool hShift, hCtrl, hAlt, hWin;
        // Ctrl+Shift だけを押して離したら手動キャプチャ。途中で他のキーやクリックがあれば取りやめる
        bool snapArmed, snapSpoiled;
        long snapTs;
        bool captureFailing;

        // ---------- フック ----------

        void HookLoop(ManualResetEvent ready, ref string error)
        {
            mouseDel = MouseProc;
            keyDel = KeyProc;
            IntPtr hMod = N.GetModuleHandle(null);
            mouseHook = N.SetWindowsHookEx(14 /*WH_MOUSE_LL*/, mouseDel, hMod, 0);
            int e1 = Marshal.GetLastWin32Error();
            keyHook = N.SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, keyDel, hMod, 0);
            int e2 = Marshal.GetLastWin32Error();
            hookTid = N.GetCurrentThreadId();
            if (mouseHook == IntPtr.Zero || keyHook == IntPtr.Zero)
            {
                error = "フックを設定できませんでした (mouse err=" + e1 + ", key err=" + e2 + ")。セキュリティソフトに止められている可能性があります";
                if (mouseHook != IntPtr.Zero) N.UnhookWindowsHookEx(mouseHook);
                if (keyHook != IntPtr.Zero) N.UnhookWindowsHookEx(keyHook);
                ready.Set();
                return;
            }
            // フォーカスの移動(テキストボックスのフォーカスイン / アウト)を知るための WinEvent。
            // WINEVENT_OUTOFCONTEXT なので、このスレッドのメッセージループでコールバックが呼ばれる
            focusDel = (h, evt, hwnd, idObject, idChild, thread, time) =>
                Enqueue(new Raw { Type = 4, Fg = hwnd, Ts = sw.ElapsedMilliseconds });
            focusHook = N.SetWinEventHook(0x8005 /*EVENT_OBJECT_FOCUS*/, 0x8005, IntPtr.Zero, focusDel, 0, 0, 0x0000 | 0x0002 /*OUTOFCONTEXT|SKIPOWNPROCESS*/);
            ready.Set();

            N.MSG msg;
            while (N.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { }

            N.UnhookWindowsHookEx(mouseHook);
            N.UnhookWindowsHookEx(keyHook);
            if (focusHook != IntPtr.Zero) N.UnhookWinEvent(focusHook);
        }

        IntPtr MouseProc(int nCode, IntPtr w, IntPtr l)
        {
            if (nCode >= 0)
            {
                int type = 0, btn = 0;
                switch ((int)w)
                {
                    case 0x201: type = 1; btn = 0; break;
                    case 0x202: type = 2; btn = 0; break;
                    case 0x204: type = 1; btn = 1; break;
                    case 0x205: type = 2; btn = 1; break;
                    case 0x207: type = 1; btn = 2; break;
                    case 0x208: type = 2; btn = 2; break;
                    case 0x20A: type = 7; break;                 // WM_MOUSEWHEEL(縦のホイール)
                }
                if (type != 0)
                {
                    if (hCtrl || hShift) snapSpoiled = true;   // Ctrl+Shift+クリックはキャプチャではない
                    var s = Marshal.PtrToStructure<N.MSLLHOOKSTRUCT>(l);
                    var raw = new Raw { Type = type, Button = btn, X = s.pt.X, Y = s.pt.Y, Ts = sw.ElapsedMilliseconds };
                    if (type == 7) raw.Vk = (short)(s.mouseData >> 16);   // ホイールの回転量(正 = 上 / 奥、負 = 下 / 手前。1 ノッチ = 120)
                    Enqueue(raw);
                }
            }
            return N.CallNextHookEx(IntPtr.Zero, nCode, w, l);
        }

        IntPtr KeyProc(int nCode, IntPtr w, IntPtr l)
        {
            if (nCode >= 0)
            {
                int m = (int)w;
                bool down = m == 0x100 || m == 0x104;
                bool up = m == 0x101 || m == 0x105;
                if (down || up)
                {
                    var s = Marshal.PtrToStructure<N.KBDLLHOOKSTRUCT>(l);
                    int vk = (int)s.vkCode;
                    bool wasCtrlOrShift = hCtrl || hShift;
                    switch (vk)
                    {
                        case 0x10: case 0xA0: case 0xA1: hShift = down; break;
                        case 0x11: case 0xA2: case 0xA3: hCtrl = down; break;
                        case 0x12: case 0xA4: case 0xA5: hAlt = down; break;
                        case 0x5B: case 0x5C: hWin = down; break;
                    }
                    bool isCtrlOrShift = vk == 0x10 || vk == 0x11 || (vk >= 0xA0 && vk <= 0xA3);
                    if (!wasCtrlOrShift && down) { snapArmed = false; snapSpoiled = false; }
                    if (down && !isCtrlOrShift) snapSpoiled = true;
                    if (down && isCtrlOrShift && hCtrl && hShift && !snapArmed) { snapArmed = true; snapTs = sw.ElapsedMilliseconds; }
                    if (up && isCtrlOrShift && snapArmed)
                    {
                        // 片方を離した時点で判定する。他のキーもクリックも無ければキャプチャ
                        if (!snapSpoiled && !hAlt && !hWin)
                            Enqueue(new Raw { Type = 6, Ts = snapTs, Fg = N.GetForegroundWindow() });
                        snapArmed = false;
                        snapSpoiled = true;   // もう片方を離すまで、再度のキャプチャはしない
                    }
                    if (down)
                    {
                        bool repeat = !keysDown.Add(vk);
                        bool alt = hAlt || (s.flags & 0x20) != 0;
                        var action = Hotkeys != null ? Hotkeys.ActionOf(vk, hCtrl, hShift, alt, hWin) : null;
                        if (action.HasValue)
                        {
                            // 記録の操作キー(既定は Ctrl+Shift+F9〜F12)。操作としては記録しない
                            //   開始・再開 : 記録中は何もしない(待機中は HotkeyListener が受ける)
                            //   取消       : 直前 1 手の取消(振り分けスレッドで処理する)
                            //   取り直し   : 同じ設定で最初から取り直す
                            //   終了       : 停止・終了
                            if (!repeat)
                            {
                                if (action == HotkeyAction.Undo) Enqueue(new Raw { Type = 5, Ts = sw.ElapsedMilliseconds });
                                else if (action == HotkeyAction.Restart) RestartRequested.Set();
                                else if (action == HotkeyAction.Stop) StopRequested.Set();
                            }
                        }
                        else
                        {
                            IntPtr fg = N.GetForegroundWindow();
                            Enqueue(new Raw
                            {
                                Type = 3, Vk = vk, Scan = (int)s.scanCode, Extended = (s.flags & 1) != 0,
                                Ctrl = hCtrl, Shift = hShift, Alt = hAlt || (s.flags & 0x20) != 0, Win = hWin,
                                Repeat = repeat, Ts = sw.ElapsedMilliseconds, Fg = fg, Focus = WinUtil.FocusOf(fg)
                            });
                        }
                    }
                    else keysDown.Remove(vk);
                }
            }
            return N.CallNextHookEx(IntPtr.Zero, nCode, w, l);
        }

        void Enqueue(Raw r)
        {
            try { rawQ.TryAdd(r); } catch (InvalidOperationException) { }
        }

        // ---------- 撮影 ----------

        void CaptureLoop()
        {
            long lastPidRefresh = 0;
            while (!captureStop)
            {
                long t0 = sw.ElapsedMilliseconds;
                try
                {
                    if (Mode == CaptureMode.App && t0 - lastPidRefresh > 1000) { RefreshPids(); lastPidRefresh = t0; }
                    if (TargetReady.WaitOne(0))
                    {
                        Frame f = CaptureFrame();
                        if (f != null) AddFrame(f);
                    }
                    ExpireAfters();
                    if (captureFailing) { captureFailing = false; Log("撮影を再開しました"); }
                }
                catch (Exception ex)
                {
                    // 画面ロック中や UAC の確認画面では CopyFromScreen が「ハンドルが無効です」で失敗し続ける。
                    // 0.1 秒ごとにログが埋まらないよう、失敗が続く間は最初の 1 回だけ出す
                    if (!captureFailing) Log("撮影できません(画面ロック中など): " + ex.Message);
                    captureFailing = true;
                }
                int wait = IntervalMs - (int)(sw.ElapsedMilliseconds - t0);
                Thread.Sleep(Math.Max(10, wait));
            }
        }

        HashSet<IntPtr> TargetSet(List<WinEntry> z)
        {
            var set = new HashSet<IntPtr>();
            switch (Mode)
            {
                case CaptureMode.Window:
                    if (N.IsWindow(TargetHwnd)) set.Add(TargetHwnd);
                    break;
                case CaptureMode.App:
                    var p = pids;
                    foreach (var w in z) if (p.Contains(w.Pid)) set.Add(w.H);
                    break;
                default:
                    IntPtr fg = WinUtil.Root(N.GetForegroundWindow());
                    if (fg != IntPtr.Zero) set.Add(fg);
                    // クリックで別のウィンドウに切り替える瞬間の「前」も撮れるよう、カーソル下のウィンドウも含める
                    N.POINT cp;
                    if (N.GetCursorPos(out cp))
                    {
                        IntPtr cr = WinUtil.Root(N.WindowFromPoint(cp));
                        if (cr != IntPtr.Zero)
                        {
                            string c = WinUtil.ClassOf(cr);
                            if (!TargetRules.IsDesktopWindow(c)) set.Add(cr);
                        }
                    }
                    break;
            }
            return set;
        }

        Frame CaptureFrame()
        {
            if (Mode == CaptureMode.AllDesktops)
            {
                // すべてのディスプレイを丸ごと撮る。ディスプレイの無い隙間(大きさや位置が揃っていない構成)は塗りつぶす
                Rectangle u = TargetRules.UnionBounds(allMonitors);
                u.Intersect(WinUtil.VirtualScreen());
                if (u.Width <= 0 || u.Height <= 0) return null;
                var ub = new Bitmap(u.Width, u.Height, PixelFormat.Format24bppRgb);
                long uts = sw.ElapsedMilliseconds;
                using (var g = Graphics.FromImage(ub))
                    g.CopyFromScreen(u.X, u.Y, 0, 0, u.Size, CopyPixelOperation.SourceCopy);
                var vis = new Region(); vis.MakeEmpty();
                foreach (var b in allMonitors) vis.Union(b);
                return new Frame { Ts = uts, Bmp = ub, Area = u, Visible = vis };
            }
            if (Mode == CaptureMode.Desktop)
            {
                // ディスプレイを丸ごと撮る。ユーザーが明示的に選んだモードなので、他アプリの塗りつぶしはしない
                Rectangle m = TargetMonitor.Bounds;
                m.Intersect(WinUtil.VirtualScreen());
                if (m.Width <= 0 || m.Height <= 0) return null;
                var mb = new Bitmap(m.Width, m.Height, PixelFormat.Format24bppRgb);
                long mts = sw.ElapsedMilliseconds;
                using (var g = Graphics.FromImage(mb))
                    g.CopyFromScreen(m.X, m.Y, 0, 0, m.Size, CopyPixelOperation.SourceCopy);
                return new Frame { Ts = mts, Bmp = mb, Area = m, Visible = new Region(m) };
            }

            var z = WinUtil.EnumZOrder();
            var targets = TargetSet(z);
            if (targets.Count == 0) return null;

            // 手前から順に見ていき、対象ウィンドウのうち「より手前の他ウィンドウに隠されていない部分」だけを可視領域にする。
            // 保存時にこの可視領域の外を塗りつぶすので、重なった他アプリの内容は画像に残らない。
            var visible = new Region(); visible.MakeEmpty();
            var occluder = new Region(); occluder.MakeEmpty();
            Rectangle area = Rectangle.Empty;
            bool any = false;
            foreach (var w in z)
            {
                if (targets.Contains(w.H))
                {
                    using (var r = new Region(w.R)) { r.Exclude(occluder); visible.Union(r); }
                    area = any ? Rectangle.Union(area, w.R) : w.R;
                    any = true;
                }
                // 自分のウィンドウ(アプリのオーバーレイなど)は撮影から除外する設定にしてあり画像に写らないので、隠す側として扱わない
                else if (w.Pid != OwnPid) occluder.Union(w.R);
            }
            occluder.Dispose();
            if (any) area.Intersect(WinUtil.VirtualScreen());
            if (!any || area.Width <= 0 || area.Height <= 0) { visible.Dispose(); return null; }

            var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
            long ts = sw.ElapsedMilliseconds;
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(area.X, area.Y, 0, 0, area.Size, CopyPixelOperation.SourceCopy);
            return new Frame { Ts = ts, Bmp = bmp, Area = area, Visible = visible };
        }

        void AddFrame(Frame f)
        {
            lock (ringLock)
            {
                ring.Add(f);
                long cutoff = f.Ts - RetainMs;
                while (ring.Count > 1 && ring[0].Ts < cutoff)
                {
                    var old = ring[0];
                    ring.RemoveAt(0);
                    old.Evicted = true;
                    if (old.Pins <= 0) DisposeFrame(old);
                }
            }
            lock (pendingLock)
            {
                foreach (var ev in pending.ToArray())
                {
                    if (ev.After == null && !ev.AfterMissing && ev.AfterDue <= f.Ts)
                    {
                        Pin(f);
                        ev.After = f;
                        TrySendLocked(ev);
                    }
                }
            }
        }

        // 対象ウィンドウが閉じた等で「後」が撮れないまま時間が過ぎた操作は、後の画像なしで確定させる
        void ExpireAfters()
        {
            long now = sw.ElapsedMilliseconds;
            lock (pendingLock)
            {
                foreach (var ev in pending.ToArray())
                {
                    if (ev.After == null && ev.AfterDue != long.MaxValue && now > ev.AfterDue + 1500)
                    {
                        ev.AfterMissing = true;
                        TrySendLocked(ev);
                    }
                }
            }
        }
    }
}
