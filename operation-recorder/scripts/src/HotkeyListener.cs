using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace OpRec
{
    // 記録の開始を待っている間だけ使うホットキー待ち受け(既定は Ctrl+Shift+F9 = 開始・再開 / Ctrl+Shift+F12 = 終了)。
    // 待機中はキーボードフックを外しておき(打鍵を一切見ない)、RegisterHotKey で決まったキーの組み合わせだけを受け取る。
    public sealed class HotkeyListener : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x, y; public uint lPrivate; }
        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        const int ID_START = 9, ID_QUIT = 12;

        public readonly ManualResetEvent StartPressed = new ManualResetEvent(false);
        public readonly ManualResetEvent QuitPressed = new ManualResetEvent(false);
        public string Error { get; private set; }

        Thread thread;
        uint tid;

        // withQuit = false なら開始のキーだけを登録する(アプリは待機中に終了のキーを使わないので、他のアプリから奪わない)
        public void Start() { Start(true); }

        public void Start(bool withQuit) { Start(HotkeySet.Defaults(), withQuit); }

        public void Start(HotkeySet keys, bool withQuit)
        {
            var start = (keys ?? HotkeySet.Defaults()).Start;
            var quit = (keys ?? HotkeySet.Defaults()).Stop;
            var ready = new ManualResetEvent(false);
            thread = new Thread(() =>
            {
                tid = GetCurrentThreadId();
                bool a = RegisterHotKey(IntPtr.Zero, ID_START, start.Modifiers, (uint)start.Key);
                int e1 = Marshal.GetLastWin32Error();
                bool b = !withQuit || RegisterHotKey(IntPtr.Zero, ID_QUIT, quit.Modifiers, (uint)quit.Key);
                int e2 = Marshal.GetLastWin32Error();
                if (!a || !b)
                {
                    Error = "ホットキーを登録できませんでした(他のアプリが同じキーを使っている可能性があります)"
                        + (a ? "" : " " + start + " err=" + e1) + (b ? "" : " " + quit + " err=" + e2);
                }
                ready.Set();
                MSG m;
                while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0)
                {
                    if (m.message != 0x0312 /*WM_HOTKEY*/) continue;
                    if ((int)m.wParam == ID_START) StartPressed.Set();
                    else if ((int)m.wParam == ID_QUIT) QuitPressed.Set();
                }
                if (a) UnregisterHotKey(IntPtr.Zero, ID_START);
                if (b && withQuit) UnregisterHotKey(IntPtr.Zero, ID_QUIT);
            });
            thread.IsBackground = true;
            thread.Start();
            ready.WaitOne(3000);
        }

        public void Dispose()
        {
            if (thread == null) return;
            PostThreadMessage(tid, 0x12 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
            thread.Join(2000);
            thread = null;
        }
    }
}
