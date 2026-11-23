using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;

namespace OpRec
{
    public sealed partial class Recorder
    {
        bool captureFailing;

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
                        if (f != null)
                        {
                            if (RecordVideoFrame != null) RecordMaskedVideoFrame(f);
                            AddFrame(f);
                        }
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
                    }
                    TrySendLocked(ev);
                }
            }
        }
    }
}
