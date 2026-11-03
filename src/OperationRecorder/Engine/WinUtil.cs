using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace OpRec
{
    // Desktop = 1 つのディスプレイ全体 / AllDesktops = すべてのディスプレイを合わせた全体
    public enum CaptureMode { Window, App, Active, Desktop, AllDesktops }

    internal sealed class Frame
    {
        public long Ts;
        public Bitmap Bmp;
        public Rectangle Area;      // 画面座標でのキャプチャ範囲
        public Region Visible;      // 画面座標で「対象ウィンドウが実際に見えている」領域。これ以外は保存時に塗りつぶす
        public int Pins;
        public bool Evicted, Disposed;
    }

    internal sealed class Raw
    {
        public int Type;            // 1=マウス押下 2=マウス解放 3=キー押下 4=フォーカス移動 5=直前1手取消 6=Ctrl+Shift キャプチャ 7=ホイール(Vk に回転量)
        public int Button;          // 0=左 1=右 2=中
        public int X, Y, Vk, Scan;
        public bool Extended, Ctrl, Shift, Alt, Win, Repeat;
        public long Ts;
        public IntPtr Fg, Focus;
    }

    internal sealed class Ev
    {
        public int Seq;
        public string Kind;
        public int Button = -1;
        public long Ts, UpTs;
        public int X, Y, EndX, EndY;
        public bool IsMouse;
        public IntPtr Root;                // 操作したトップレベルウィンドウ
        public Rectangle WinFrame;
        public Rectangle? MarkRect;
        public Dictionary<string, object> Rec = new Dictionary<string, object>();
        public Frame Before, After;
        public long AfterDue = long.MaxValue;
        public bool InfoDone, Sent, AfterMissing;
        public bool WantAfter = true;      // 撮影ルールで「操作後を撮る」が切られていれば false
        public int WheelNet;               // ホイール: まとめた回転の合計(上 = 正 / 下 = 負。目盛りの数)。0 なら元に戻したので記録しない
        public long WheelLastTs;
        // 画像の撮影時刻のずれを測る基準。0 なら既定(操作前=Ts、操作後=マウスは UpTs / それ以外は Ts)
        public long BeforeRefTs, AfterRefTs;
        public string BeforeRefName, AfterRefName;
    }

    // テキストボックスにフォーカスが入ってから外れるまでの追跡
    internal sealed class FocusCandidate
    {
        public string Key;                       // UI Automation の RuntimeId
        public System.Windows.Automation.AutomationElement Element;
        public IntPtr Root;
        public long TsIn, LastTypeTs;
        public Frame Before;                     // フォーカスインの時点の画面
        public bool Typed;
        public Dictionary<string, object> ValueIn, LastValue, Uia, Win32, Window;
        public Rectangle WinFrame;
        public Rectangle? MarkRect;
    }

    internal sealed class SavedRec
    {
        public int Seq;
        public long Ts;
        public string Kind, Line;
        public List<string> Files = new List<string>();
    }

    internal struct WinEntry { public IntPtr H; public Rectangle R; public uint Pid; }

    public static class WinUtil
    {
        public static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(256);
            N.GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string TextOf(IntPtr h)
        {
            var sb = new StringBuilder(512);
            N.GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static IntPtr Root(IntPtr h)
        {
            return h == IntPtr.Zero ? IntPtr.Zero : N.GetAncestor(h, N.GA_ROOT);
        }

        public static uint PidOf(IntPtr h)
        {
            uint pid;
            N.GetWindowThreadProcessId(h, out pid);
            return pid;
        }

        public static string ProcName(uint pid)
        {
            try { using (var p = Process.GetProcessById((int)pid)) return p.ProcessName; }
            catch { return null; }
        }

        public static IntPtr FocusOf(IntPtr anyWindowOfThread)
        {
            if (anyWindowOfThread == IntPtr.Zero) return IntPtr.Zero;
            uint pid;
            uint tid = N.GetWindowThreadProcessId(anyWindowOfThread, out pid);
            var gi = new N.GUITHREADINFO();
            gi.cbSize = Marshal.SizeOf<N.GUITHREADINFO>();
            return N.GetGUIThreadInfo(tid, ref gi) ? gi.hwndFocus : IntPtr.Zero;
        }

        // 撮影範囲は GetWindowRect ではなく DWMWA_EXTENDED_FRAME_BOUNDS から取る。
        // GetWindowRect は見えない透明リサイズ枠を含み、その枠に背後の別ウィンドウが写り込む
        // (windows-ui-automation スキルで実際に起きた事故)。
        public static Rectangle GetFrame(IntPtr h)
        {
            N.RECT r;
            if (N.DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf<N.RECT>()) != 0)
            {
                if (!N.GetWindowRect(h, out r)) return Rectangle.Empty;
                r.Left += 12; r.Top += 12; r.Right -= 12; r.Bottom -= 12;
            }
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        public static Rectangle VirtualScreen()
        {
            return new Rectangle(N.GetSystemMetrics(76), N.GetSystemMetrics(77), N.GetSystemMetrics(78), N.GetSystemMetrics(79));
        }

        // 画面上に実際に表示されているトップレベルウィンドウを、手前から奥の順で返す
        internal static List<WinEntry> EnumZOrder()
        {
            var list = new List<WinEntry>();
            N.EnumWindows((h, l) =>
            {
                if (!N.IsWindowVisible(h) || N.IsIconic(h)) return true;
                int cloaked;
                if (N.DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
                Rectangle r = GetFrame(h);
                if (r.Width <= 0 || r.Height <= 0) return true;
                list.Add(new WinEntry { H = h, R = r, Pid = PidOf(h) });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static IntPtr FindWindowByTitle(string part)
        {
            foreach (var w in EnumZOrder())
            {
                string t = TextOf(w.H);
                if (t.Length > 0 && t.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0 && Root(w.H) == w.H) return w.H;
            }
            return IntPtr.Zero;
        }

        public static IntPtr FindWindowByProcess(string name)
        {
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            foreach (var w in EnumZOrder())
            {
                if (TextOf(w.H).Length == 0 || Root(w.H) != w.H) continue;
                if (string.Equals(ProcName(w.Pid), name, StringComparison.OrdinalIgnoreCase)) return w.H;
            }
            return IntPtr.Zero;
        }

        public static bool IsAlive(IntPtr h) { return h != IntPtr.Zero && N.IsWindow(h); }

        // ---------- プログラムの情報(Windows のステップ記録ツールと同じ: 製品名・バージョン・会社名・コマンドライン・exe 名) ----------

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int ret);
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        static readonly Dictionary<uint, Dictionary<string, object>> programCache = new Dictionary<uint, Dictionary<string, object>>();

        public static Dictionary<string, object> ProgramInfo(uint pid)
        {
            lock (programCache)
            {
                Dictionary<string, object> d;
                if (programCache.TryGetValue(pid, out d)) return d;
                d = new Dictionary<string, object>();
                string path = null, cmd = null;
                IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h != IntPtr.Zero)
                {
                    try
                    {
                        var sb = new StringBuilder(1024);
                        int size = sb.Capacity;
                        if (QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();
                        cmd = CommandLineOf(h);
                    }
                    finally { CloseHandle(h); }
                }
                d["exe"] = path != null ? System.IO.Path.GetFileName(path) : (ProcName(pid) ?? "") + ".exe";
                d["path"] = path;
                d["commandLine"] = cmd;
                try
                {
                    if (path != null)
                    {
                        var vi = FileVersionInfo.GetVersionInfo(path);
                        d["name"] = !string.IsNullOrEmpty(vi.FileDescription) ? vi.FileDescription : vi.ProductName;
                        d["product"] = vi.ProductName;
                        d["version"] = !string.IsNullOrEmpty(vi.ProductVersion) ? vi.ProductVersion : vi.FileVersion;
                        d["company"] = vi.CompanyName;
                    }
                }
                catch { }
                programCache[pid] = d;
                return d;
            }
        }

        // ProcessCommandLineInformation(= 60。Windows 8.1 以降)で、他のプロセスのコマンドラインを読む
        static string CommandLineOf(IntPtr h)
        {
            int len = 0x10000;
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                int ret;
                if (NtQueryInformationProcess(h, 60, buf, len, out ret) != 0) return null;
                // 先頭は UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; }
                int bytes = Marshal.ReadInt16(buf) & 0xFFFF;
                IntPtr str = Marshal.ReadIntPtr(buf, IntPtr.Size);
                return bytes > 0 && str != IntPtr.Zero ? Marshal.PtrToStringUni(str, bytes / 2) : null;
            }
            catch { return null; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public static string Describe(IntPtr h)
        {
            if (h == IntPtr.Zero) return "(なし)";
            return string.Format("\"{0}\" [{1}] {2}.exe hwnd=0x{3:X}", TextOf(h), ClassOf(h), ProcName(PidOf(h)), h.ToInt64());
        }

        internal static Dictionary<string, object> Pt(int x, int y)
        {
            return new Dictionary<string, object> { { "x", x }, { "y", y } };
        }

        internal static Dictionary<string, object> Rc(Rectangle r)
        {
            return new Dictionary<string, object> { { "x", r.X }, { "y", r.Y }, { "w", r.Width }, { "h", r.Height } };
        }

        internal static string Hex(IntPtr h) { return "0x" + h.ToInt64().ToString("X"); }

        internal static string Trunc(string s, int max)
        {
            if (s == null) return null;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
