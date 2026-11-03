using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace OpRec
{
    public sealed class MonitorInfo
    {
        public int Index;            // 1 から。左上から順(X → Y)に並べた番号
        public string DeviceName;    // \\.\DISPLAY1 など
        public Rectangle Bounds;     // 物理ピクセルの画面座標
        public bool IsPrimary;
        public int ScalePercent;
        internal IntPtr Handle;

        public string Describe()
        {
            return string.Format("{0}x{1} ({2}%){3} {4} 位置({5},{6})",
                Bounds.Width, Bounds.Height, ScalePercent, IsPrimary ? " メイン" : "", DeviceName.Replace(@"\\.\", ""), Bounds.X, Bounds.Y);
        }
    }

    public static class Monitors
    {
        // pwsh のメインスレッドは DPI 非対応のことがあり、そのままだと拡大率に応じて縮んだ座標が返る。
        // 記録スレッドと同じ物理ピクセルで揃えるため、呼び出しの間だけスレッドを per-monitor aware にする
        public static List<MonitorInfo> GetAll()
        {
            IntPtr old = N.SetThreadDpiAwarenessContext(N.DPI_PER_MONITOR_AWARE_V2);
            try
            {
                var list = new List<MonitorInfo>();
                N.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref N.RECT rc, IntPtr d) =>
                {
                    var mi = new N.MONITORINFOEX();
                    mi.cbSize = Marshal.SizeOf<N.MONITORINFOEX>();
                    if (!N.GetMonitorInfo(h, ref mi)) return true;
                    uint dx, dy;
                    int scale = N.GetDpiForMonitor(h, 0, out dx, out dy) == 0 ? (int)Math.Round(dx * 100.0 / 96) : 100;
                    list.Add(new MonitorInfo
                    {
                        Handle = h,
                        DeviceName = mi.szDevice,
                        Bounds = Rectangle.FromLTRB(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom),
                        IsPrimary = (mi.dwFlags & 1) != 0,
                        ScalePercent = scale,
                    });
                    return true;
                }, IntPtr.Zero);
                list.Sort((a, b) => a.Bounds.X != b.Bounds.X ? a.Bounds.X.CompareTo(b.Bounds.X) : a.Bounds.Y.CompareTo(b.Bounds.Y));
                for (int i = 0; i < list.Count; i++) list[i].Index = i + 1;
                return list;
            }
            finally
            {
                if (old != IntPtr.Zero) N.SetThreadDpiAwarenessContext(old);
            }
        }

        public static MonitorInfo Get(int index)
        {
            foreach (var m in GetAll()) if (m.Index == index) return m;
            return null;
        }

        // ウィンドウが(いちばん広く)映っているディスプレイ
        public static MonitorInfo OfWindow(IntPtr hwnd)
        {
            IntPtr h = N.MonitorFromWindow(hwnd, 2 /*MONITOR_DEFAULTTONEAREST*/);
            foreach (var m in GetAll()) if (m.Handle == h) return m;
            return null;
        }
    }
}
