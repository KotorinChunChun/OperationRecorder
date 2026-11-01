using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace OperationRecorderApp.Replay;

public sealed class ReplayHotkeys : IDisposable
{
    readonly ConcurrentDictionary<string, byte> pressed = new();
    readonly Thread thread;
    readonly ManualResetEvent ready = new(false);
    volatile bool enabled = true;
    uint tid;
    public string Error { get; private set; }
    public bool Enabled { get => enabled; set { enabled = value; pressed.Clear(); } }
    public bool Consume(string key) => pressed.TryRemove(key, out _);
    public ReplayHotkeys(string stop, IEnumerable<string> starts, CancellationTokenSource cancel)
    {
        var keys = new[] { stop }.Concat(starts).ToArray();
        thread = new Thread(() =>
        {
            tid = GetCurrentThreadId(); PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            var registered = new List<int>();
            try
            {
                for (int id = 0; id < keys.Length; id++)
                {
                    int[] values = ReplayKeys.Parse(keys[id], true);
                    uint modifiers = 0x4000;
                    foreach (var value in values.Take(values.Length - 1)) modifiers |= value switch { 16 => 4u, 17 => 2u, 18 => 1u, 91 => 8u, _ => 0 };
                    if (!RegisterHotKey(IntPtr.Zero, id + 100, modifiers, (uint)values.Last())) { Error = "開始/非常停止キーを登録できません。他のアプリと重複していないか確認してください。"; return; }
                    registered.Add(id + 100);
                }
                ready.Set();
                while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    if (message.Message != 0x312) continue;
                    int id = (int)message.WParam - 100;
                    if (id == 0) cancel.Cancel();
                    else if (enabled && id > 0 && id < keys.Length) pressed.TryAdd(keys[id], 0);
                }
            }
            catch (Exception) { Error = "ホットキー待受けを開始できません。"; }
            finally { foreach (int id in registered) UnregisterHotKey(IntPtr.Zero, id); ready.Set(); }
        }) { IsBackground = true, Name = "replay-hotkeys" };
        thread.Start();
        if (!ready.WaitOne(3000)) Error = "ホットキー待受けが応答しません。";
    }
    public void Dispose() { if (thread.IsAlive) { PostThreadMessage(tid, 0x12, IntPtr.Zero, IntPtr.Zero); thread.Join(3000); } }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr Window; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
