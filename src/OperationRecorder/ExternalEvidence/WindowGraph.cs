using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using OpRec;

namespace OperationRecorderApp.ExternalEvidence;

public sealed record WindowObservation(Guid WindowId, int Generation, long Hwnd, long ParentHwnd, long OwnerHwnd,
    int Pid, bool IsChild, bool Visible, bool Minimized, bool Cloaked, Rectangle Bounds, int Dpi, string Relation);
public sealed record WindowGraphSnapshot(long Revision, bool Truncated, IReadOnlyList<WindowObservation> Windows);
public sealed record WindowGraphInput(long Hwnd, long ParentHwnd, long OwnerHwnd, int Pid, bool IsChild,
    bool Visible, bool Minimized, bool Cloaked, Rectangle Bounds, int Dpi, string Relation);

/// <summary>Observed HWND lifetimes; this is not an atomic rendering or secrecy attestation.</summary>
public sealed class WindowGraphTracker
{
    readonly object sync = new();
    readonly Dictionary<long, WindowObservation> current = new();
    readonly Dictionary<long, int> generations = new();
    readonly int maxTrackedHandles;
    long revision;

    public WindowGraphTracker(int maxTrackedHandles = 4096)
    {
        if (maxTrackedHandles < 1 || maxTrackedHandles > 65536) throw new ArgumentOutOfRangeException(nameof(maxTrackedHandles));
        this.maxTrackedHandles = maxTrackedHandles;
    }

    public void MarkDestroyed(long hwnd)
    {
        lock (sync)
        {
            if (current.Remove(hwnd)) revision++;
        }
    }
    internal bool IsStillTracked(WindowObservation previous)
    {
        lock (sync)
            return current.TryGetValue(previous.Hwnd, out var observed) &&
                observed.WindowId == previous.WindowId && observed.Generation == previous.Generation;
    }
    public WindowGraphSnapshot Update(IReadOnlyList<WindowGraphInput> inputs, bool truncated = false)
    {
        if (inputs == null || inputs.Count > 1024 || inputs.Any(w => w.Hwnd == 0 || w.Pid <= 0) || inputs.Select(w => w.Hwnd).Distinct().Count() != inputs.Count)
            throw new ArgumentException("invalid-window-graph");
        lock (sync)
        {
            if (generations.Count + inputs.Count(w => !generations.ContainsKey(w.Hwnd)) > maxTrackedHandles)
                throw new EvidenceUnavailableException("window-history-limit");
            var observed = new List<WindowObservation>(inputs.Count);
            var seen = new HashSet<long>();
            foreach (var w in inputs)
            {
                seen.Add(w.Hwnd);
                bool sameLifetime = current.TryGetValue(w.Hwnd, out var previous) && previous.Pid == w.Pid &&
                    previous.ParentHwnd == w.ParentHwnd && previous.OwnerHwnd == w.OwnerHwnd && previous.IsChild == w.IsChild && previous.Relation == w.Relation;
                int generation = sameLifetime ? previous.Generation : generations.GetValueOrDefault(w.Hwnd) + 1;
                if (generation <= 0) throw new EvidenceUnavailableException("window-generation-unavailable");
                var next = new WindowObservation(sameLifetime ? previous.WindowId : Guid.NewGuid(), generation,
                    w.Hwnd, w.ParentHwnd, w.OwnerHwnd, w.Pid, w.IsChild, w.Visible, w.Minimized, w.Cloaked, w.Bounds, w.Dpi, w.Relation);
                if (previous != next) revision++;
                current[w.Hwnd] = next; generations[w.Hwnd] = generation; observed.Add(next);
            }
            // Missing nodes are only evidence of removal after a complete enumeration.
            if (!truncated) foreach (long hwnd in current.Keys.Where(h => !seen.Contains(h)).ToArray()) { current.Remove(hwnd); revision++; }
            return new(revision, truncated, observed);
        }
    }
}

/// <summary>Read-only native graph of a fixed process and root HWND. Never reads titles, UIA names, or values.</summary>
public sealed class WindowGraphRegistry : IDisposable
{
    const uint WsChild = 0x40000000, GwOwner = 4, GaParent = 1;
    const uint EventObjectCreate = 0x8000, EventObjectDestroy = 0x8001, EventObjectLast = 0x80FF;
    readonly WindowGraphTracker tracker = new();
    readonly object sync = new();
    readonly int currentSession;
    readonly string currentSid;
    EvidenceTarget bound;
    Thread hookThread;
    N.WinEventProc callback;
    uint hookThreadId;
    readonly ManualResetEventSlim hookReady = new();
    volatile bool rootDestroyed, hookHealthy, disposed;
    long events;

    public WindowGraphRegistry()
    {
        using var process = Process.GetCurrentProcess(); currentSession = process.SessionId;
        using var identity = WindowsIdentity.GetCurrent(); currentSid = identity.User?.Value;
    }
    [DllImport("user32.dll")] static extern bool EnumWindows(N.EnumWindowsProc callback, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, N.EnumWindowsProc callback, IntPtr data);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] static extern bool PeekMessage(out N.MSG message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref N.MSG message);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref N.MSG message);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    public bool MatchesProcess(EvidenceTarget root)
    {
        lock (sync)
        {
            if (disposed || rootDestroyed || bound != null && bound != root || root == null || root.Pid <= 0 || root.WindowHandle == 0 ||
                root.WindowsSessionId != currentSession || string.IsNullOrWhiteSpace(currentSid) || !System.IO.Path.IsPathFullyQualified(root.ExePath)) return false;
            try
            {
                using var process = Process.GetProcessById(root.Pid);
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != root.ProcessStartTimeUtc.UtcTicks || process.SessionId != root.WindowsSessionId ||
                    !string.Equals(System.IO.Path.GetFullPath(process.MainModule.FileName), System.IO.Path.GetFullPath(root.ExePath), StringComparison.OrdinalIgnoreCase)) return false;
                if (!OpenProcessToken(process.Handle, 8, out var token)) return false;
                using (token) using (var identity = new WindowsIdentity(token.DangerousGetHandle())) if (identity.User?.Value != currentSid) return false;
                IntPtr hwnd = new(root.WindowHandle);
                if (!N.IsWindow(hwnd) || N.GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid != root.Pid ||
                    N.GetAncestor(hwnd, N.GA_ROOT) != hwnd || ((ulong)N.GetWindowLongPtr(hwnd, N.GWL_STYLE).ToInt64() & WsChild) != 0) return false;
                // Visibility, minimized, cloaked, and off-screen states do not change process identity.
                if (bound == null) bound = root;
                return true;
            }
            catch { return false; }
        }
    }
    // The original hook remains active after its root is destroyed. Use its history only
    // to confirm existing windows, never to retarget this registry.
    internal bool IsStillTracked(WindowObservation previous) =>
        !disposed && hookHealthy && tracker.IsStillTracked(previous);

    internal static WindowGraphInput[] ObserveTransitionTopWindows(EvidenceTarget root, int maxWindows)
    {
        var inputs = new List<WindowGraphInput>();
        bool valid = true;
        IntPtr previousDpi = N.SetThreadDpiAwarenessContext(N.DPI_PER_MONITOR_AWARE_V2);
        try
        {
            var old = new IntPtr(root.WindowHandle);
            N.EnumWindowsProc read = (window, data) =>
            {
                long hwnd = window.ToInt64();
                if (PidOf(hwnd) != root.Pid && hwnd != root.WindowHandle &&
                    !InChain(GetWindow(window, GwOwner).ToInt64(), root.WindowHandle, root.Pid, true))
                    return true;
                if (inputs.Count >= maxWindows) return false;
                try { inputs.Add(Read(hwnd, root.Pid, root.WindowHandle)); }
                catch (EvidenceUnavailableException) { valid = false; return false; }
                return true;
            };
            if (!EnumWindows(read, IntPtr.Zero) || !valid ||
                N.IsWindow(old) && !inputs.Any(window => window.Hwnd == root.WindowHandle))
                throw new EvidenceUnavailableException("transition-unconfirmed");
            return inputs.ToArray();
        }
        finally { if (previousDpi != IntPtr.Zero) N.SetThreadDpiAwarenessContext(previousDpi); }
    }
    void EnsureHook(EvidenceTarget root)
    {
        lock (sync)
        {
            if (hookThread == null)
            {
                hookThread = new Thread(() =>
                {
                    hookThreadId = N.GetCurrentThreadId();
                    PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // Create the queue before exposing readiness.
                    callback = (hook, evt, hwnd, objectId, childId, thread, time) =>
                    {
                        if (hwnd == IntPtr.Zero || objectId != 0 || childId != 0) return;
                        Interlocked.Increment(ref events);
                        if (evt == EventObjectDestroy)
                        {
                            tracker.MarkDestroyed(hwnd.ToInt64());
                            if (hwnd.ToInt64() == root.WindowHandle) rootDestroyed = true;
                        }
                    };
                    IntPtr handle = N.SetWinEventHook(EventObjectCreate, EventObjectLast, IntPtr.Zero, callback, (uint)root.Pid, 0, N.WINEVENT_OUTOFCONTEXT);
                    hookHealthy = handle != IntPtr.Zero; hookReady.Set();
                    if (handle == IntPtr.Zero) return;
                    try { while (N.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); } }
                    finally { N.UnhookWinEvent(handle); hookHealthy = false; }
                }) { IsBackground = true, Name = "evidence-window-graph" };
                hookThread.Start();
            }
        }
        if (!hookReady.Wait(2000) || !hookHealthy) throw new EvidenceUnavailableException("window-observation-unavailable");
    }
    static int PidOf(long hwnd)
    {
        if (hwnd == 0 || N.GetWindowThreadProcessId(new IntPtr(hwnd), out uint pid) == 0 || pid > int.MaxValue) return 0;
        return (int)pid;
    }
    static bool InChain(long start, long root, int pid, bool owners)
    {
        var seen = new HashSet<long>();
        for (long h = start; h != 0 && seen.Count < 32 && seen.Add(h);)
        {
            if (h == root) return true;
            if (PidOf(h) != pid) return false;
            h = owners ? GetWindow(new IntPtr(h), GwOwner).ToInt64() : N.GetAncestor(new IntPtr(h), GaParent).ToInt64();
        }
        return false;
    }
    static bool ParentAndOwnerChain(long start, long root, int pid)
    {
        var seen = new HashSet<long>();
        for (long h = start; h != 0 && seen.Count < 32 && seen.Add(h);)
        {
            if (h == root) return true;
            if (PidOf(h) != pid) return false;
            var handle = new IntPtr(h);
            bool child = ((ulong)N.GetWindowLongPtr(handle, N.GWL_STYLE).ToInt64() & WsChild) != 0;
            h = child ? N.GetAncestor(handle, GaParent).ToInt64() : GetWindow(handle, GwOwner).ToInt64();
        }
        return false;
    }
    static WindowGraphInput Read(long hwnd, int targetPid, long root)
    {
        IntPtr handle = new(hwnd);
        int pid = PidOf(hwnd);
        if (pid == 0 || !N.IsWindow(handle)) throw new EvidenceUnavailableException("window-changed");
        bool child = ((ulong)N.GetWindowLongPtr(handle, N.GWL_STYLE).ToInt64() & WsChild) != 0;
        long parent = child ? N.GetAncestor(handle, GaParent).ToInt64() : 0;
        long owner = GetWindow(handle, GwOwner).ToInt64();
        string relation = pid != targetPid ? "foreign" : hwnd == root ? "root" :
            child && ParentAndOwnerChain(parent, root, targetPid) ? "child" :
            InChain(owner, root, targetPid, true) ? "owned" : "unowned";
        // Foreign candidates are relationship evidence only. Do not inspect their geometry or state.
        if (pid != targetPid) return new(hwnd, parent, owner, pid, child, false, false, false, Rectangle.Empty, 0, relation);
        bool hasBounds = N.DwmGetWindowAttribute(handle, N.DWMWA_EXTENDED_FRAME_BOUNDS, out N.RECT bounds, Marshal.SizeOf<N.RECT>()) == 0;
        // Child HWNDs may not have a DWM extended-frame attribute. This fallback is structural
        // geometry only; it does not claim a capture or a pixel-to-WGC mapping.
        if (!hasBounds && child) hasBounds = N.GetWindowRect(handle, out bounds);
        var frame = hasBounds ? Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom) : Rectangle.Empty;
        bool cloaked = N.DwmGetWindowAttribute(handle, N.DWMWA_CLOAKED, out int cloak, sizeof(int)) != 0 || cloak != 0;
        int dpi = (int)N.GetDpiForWindow(handle);
        if (PidOf(hwnd) != pid || !N.IsWindow(handle)) throw new EvidenceUnavailableException("window-changed");
        return new(hwnd, parent, owner, pid, child, N.IsWindowVisible(handle), N.IsIconic(handle), cloaked, frame, dpi, relation);
    }
    public WindowGraphSnapshot Snapshot(EvidenceTarget root, int maxWindows = 32)
    {
        IntPtr previousDpi = N.SetThreadDpiAwarenessContext(N.DPI_PER_MONITOR_AWARE_V2);
        try { return SnapshotPhysical(root, maxWindows); }
        finally { if (previousDpi != IntPtr.Zero) N.SetThreadDpiAwarenessContext(previousDpi); }
    }
    WindowGraphSnapshot SnapshotPhysical(EvidenceTarget root, int maxWindows)
    {
        if (maxWindows < 1 || maxWindows > 256) throw new ArgumentOutOfRangeException(nameof(maxWindows));
        if (!MatchesProcess(root)) throw new EvidenceUnavailableException("target-mismatch");
        EnsureHook(root);
        long before = Interlocked.Read(ref events);
        bool truncated = false;
        var handles = new HashSet<long>();
        var tops = new List<long>();
        handles.Add(root.WindowHandle); tops.Add(root.WindowHandle);
        // Only PID and owner links are examined before selecting candidates. No captions or UIA.
        N.EnumWindowsProc enumerateTop = (window, data) =>
        {
            long h = window.ToInt64(); int pid = PidOf(h);
            if (pid == root.Pid || InChain(GetWindow(window, GwOwner).ToInt64(), root.WindowHandle, root.Pid, true))
            {
                if (handles.Add(h)) tops.Add(h);
                if (handles.Count > maxWindows) { truncated = true; return false; }
            }
            return true;
        };
        EnumWindows(enumerateTop, IntPtr.Zero);
        foreach (long top in tops.ToArray())
        {
            if (handles.Count > maxWindows) break;
            if (PidOf(top) != root.Pid) continue;
            N.EnumWindowsProc enumerateChild = (window, data) =>
            {
                handles.Add(window.ToInt64());
                if (handles.Count > maxWindows) { truncated = true; return false; }
                return true;
            };
            EnumChildWindows(new IntPtr(top), enumerateChild, IntPtr.Zero);
        }
        var inputs = new List<WindowGraphInput>();
        foreach (long hwnd in handles.OrderBy(h => h == root.WindowHandle ? 0 : 1).ThenBy(h => h).Take(maxWindows))
        {
            try { inputs.Add(Read(hwnd, root.Pid, root.WindowHandle)); }
            catch (EvidenceUnavailableException) { truncated = true; }
        }
        if (!MatchesProcess(root) || !hookHealthy) throw new EvidenceUnavailableException("target-mismatch");
        long after = Interlocked.Read(ref events);
        if (before != after) truncated = true;
        var snapshot = tracker.Update(inputs, truncated);
        long latest = Interlocked.Read(ref events);
        if (rootDestroyed) throw new EvidenceUnavailableException("target-mismatch");
        return snapshot with { Revision = snapshot.Revision + latest, Truncated = snapshot.Truncated || after != latest };
    }
    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; }
        if (hookThreadId != 0) N.PostThreadMessage(hookThreadId, N.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        hookThread?.Join(1000);
    }
}
