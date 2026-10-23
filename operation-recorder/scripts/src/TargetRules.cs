using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace OpRec
{
    // 記録の対象を決める判断のうち、画面やウィンドウに頼らない部分(自動テストから直接呼べる)
    public static class TargetRules
    {
        // デスクトップ(壁紙・アイコンの面)のウィンドウのクラス名
        public static bool IsDesktopWindow(string className) => className == "Progman" || className == "WorkerW";

        // デスクトップ・タスクバーなど、記録の対象にしないウィンドウのクラス名
        public static bool IsShellWindow(string className) =>
            IsDesktopWindow(className) || className == "Shell_TrayWnd" || className == "Shell_SecondaryTrayWnd";

        // 前面のウィンドウを記録の対象にできるか(自分自身・デスクトップ・タスクバーは対象にしない)
        public static bool IsUsableForeground(IntPtr root, uint pid, uint ownPid, string className) =>
            root != IntPtr.Zero && pid != 0 && pid != ownPid && !IsShellWindow(className);

        // ウィンドウがいちばん広く映っているディスプレイ。どれとも重ならなければ、中心がいちばん近いもの
        public static MonitorInfo MonitorFor(Rectangle frame, IList<MonitorInfo> monitors)
        {
            if (monitors == null || monitors.Count == 0) return null;
            MonitorInfo best = null;
            long bestArea = 0;
            foreach (var m in monitors)
            {
                var r = Rectangle.Intersect(m.Bounds, frame);
                long a = r.IsEmpty ? 0 : (long)r.Width * r.Height;
                if (a > bestArea) { bestArea = a; best = m; }
            }
            if (best != null) return best;
            var c = new Point(frame.X + frame.Width / 2, frame.Y + frame.Height / 2);
            return monitors.OrderBy(m => Dist2(c, m.Bounds)).First();
        }

        static long Dist2(Point p, Rectangle r)
        {
            long dx = p.X < r.Left ? r.Left - p.X : p.X >= r.Right ? p.X - r.Right + 1 : 0;
            long dy = p.Y < r.Top ? r.Top - p.Y : p.Y >= r.Bottom ? p.Y - r.Bottom + 1 : 0;
            return dx * dx + dy * dy;
        }

        // 記録の対象の名前(アプリのオーバーレイのタイトルなどに出す)。
        // pickPending: 「最初にクリックしたもの」をまだ選んでいない / monitorIndex: Desktop のディスプレイ番号(0 = なし)
        public static string Describe(CaptureMode mode, bool pickPending, string windowTitle, string processName, int monitorIndex, Size monitorSize)
        {
            switch (mode)
            {
                case CaptureMode.AllDesktops: return "すべてのディスプレイ";
                case CaptureMode.Active: return "操作したウィンドウ(その都度)";
            }
            if (pickPending) return mode == CaptureMode.Desktop ? "記録したいディスプレイのウィンドウをクリックしてください" : "記録したいウィンドウをクリックしてください";
            if (mode == CaptureMode.Desktop)
                return monitorIndex > 0 ? "ディスプレイ " + monitorIndex + "(" + monitorSize.Width + "x" + monitorSize.Height + ")" : "ディスプレイ";
            string title = string.IsNullOrWhiteSpace(windowTitle) ? null : windowTitle.Trim();
            if (mode == CaptureMode.App)
            {
                string proc = string.IsNullOrWhiteSpace(processName) ? null : processName.Trim();
                if (title != null && proc != null) return title + "(" + proc + " 全体)";
                if (proc != null) return proc + "(アプリ全体)";
            }
            return title ?? "(名前のないウィンドウ)";
        }

        // すべてのディスプレイを合わせた範囲(AllDesktops モードで撮る範囲)
        public static Rectangle UnionBounds(IEnumerable<Rectangle> bounds)
        {
            Rectangle u = Rectangle.Empty;
            bool any = false;
            foreach (var b in bounds) { u = any ? Rectangle.Union(u, b) : b; any = true; }
            return u;
        }
    }

    // 撮影の間隔。広い範囲を撮るときは、直近の画面を持っておくメモリが増えすぎないよう間隔を広げる
    public static class CaptureBudget
    {
        public const long DefaultBudgetBytes = 256L * 1024 * 1024;   // リングに持つ画面の合計(24bpp)の上限の目安

        public static int IntervalFor(long width, long height, int baseIntervalMs, int retainMs, long budgetBytes = DefaultBudgetBytes)
        {
            if (width <= 0 || height <= 0 || retainMs <= 0) return baseIntervalMs;
            long frameBytes = width * height * 3;
            double frames = Math.Max(1.0, (double)budgetBytes / frameBytes);   // 上限内に持てる枚数
            int needed = (int)Math.Ceiling(retainMs / frames);
            return Math.Max(baseIntervalMs, needed);
        }
    }
}
