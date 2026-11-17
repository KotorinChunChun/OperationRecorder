using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Microsoft.Win32.SafeHandles;
using OpRec;

namespace OperationRecorderApp.ExternalEvidence;

public sealed class WindowsEvidenceValidator : IEvidenceTargetValidator, IDisposable
{
    readonly int ownerSession;
    readonly string ownerSid = WindowsIdentity.GetCurrent().User.Value;
    EvidenceTarget bound;
    volatile bool destroyed;
    Thread eventThread;
    uint threadId;
    readonly ManualResetEventSlim ready = new();
    N.WinEventProc callback;
    public WindowsEvidenceValidator() { using var p = Process.GetCurrentProcess(); ownerSession = p.SessionId; }
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    public bool Matches(EvidenceTarget target)
    {
        try
        {
            if (target == null || target.Pid <= 0 || target.WindowHandle == 0 || !Path.IsPathFullyQualified(target.ExePath) || target.WindowsSessionId != ownerSession) return false;
            using var process = Process.GetProcessById(target.Pid);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != target.ProcessStartTimeUtc.UtcTicks || process.SessionId != target.WindowsSessionId ||
                !string.Equals(Path.GetFullPath(process.MainModule.FileName), Path.GetFullPath(target.ExePath), StringComparison.OrdinalIgnoreCase)) return false;
            if (!OpenProcessToken(process.Handle, 8, out var token)) return false;
            using (token) using (var identity = new WindowsIdentity(token.DangerousGetHandle())) if (identity.User.Value != ownerSid) return false;
            IntPtr hwnd = new(target.WindowHandle);
            if (!N.IsWindow(hwnd) || !N.IsWindowVisible(hwnd) || N.IsIconic(hwnd) || N.GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid != target.Pid || N.GetAncestor(hwnd, 2) != hwnd) return false;
            if (bound == null)
            {
                bound = target;
                eventThread = new Thread(() =>
                {
                    threadId = N.GetCurrentThreadId();
                    callback = (hook, evt, window, obj, child, tid, time) => { if (window.ToInt64() == target.WindowHandle && obj == 0 && child == 0) destroyed = true; };
                    IntPtr handle = N.SetWinEventHook(0x8001, 0x8001, IntPtr.Zero, callback, (uint)target.Pid, 0, 0);
                    if (handle == IntPtr.Zero) destroyed = true;
                    ready.Set();
                    if (handle != IntPtr.Zero) { while (N.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { } N.UnhookWinEvent(handle); }
                }) { IsBackground = true, Name = "evidence-window-identity" };
                eventThread.Start();
                if (!ready.Wait(2000)) return false;
            }
            return !destroyed && bound == target;
        }
        catch { return false; }
    }
    public void Dispose() { if (threadId != 0) N.PostThreadMessage(threadId, 0x12, IntPtr.Zero, IntPtr.Zero); eventThread?.Join(1000); }
}

// The provider abstraction exposes a Name getter solely to make forbidden reads observable in tests.
// Production and traversal never call it, including on unknown/authentication elements.
public interface IProtectedEvidenceElement
{
    bool IsPassword { get; }
    int ProcessId { get; }
    int ControlTypeId { get; }
    Rectangle Bounds { get; }
    bool Enabled { get; }
    string Name { get; }
    IProtectedEvidenceElement FirstChild();
    IProtectedEvidenceElement NextSibling();
}
public static class ProtectedEvidenceUi
{
    public static EvidenceUi Collect(IProtectedEvidenceElement root, int pid, Rectangle area, EvidencePolicy policy, CancellationToken token)
    {
        var nodes = new List<EvidenceNode>(); bool truncated = false; int visited = 0;
        void Visit(IProtectedEvidenceElement element, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (element == null) return;
            if (depth > policy.MaxDepth || ++visited > policy.MaxElements) { truncated = true; return; }
            // Privacy is checked before any descriptive property. No string content is ever read.
            bool password = element.IsPassword;
            if (element.ProcessId != pid) { truncated = true; return; }
            var rectangle = Rectangle.Intersect(element.Bounds, area);
            nodes.Add(new(element.ControlTypeId, rectangle.X - area.X, rectangle.Y - area.Y, rectangle.Width, rectangle.Height, password, element.Enabled));
            if (password) return; // Authentication subtrees may contain custom child providers.
            for (var child = element.FirstChild(); child != null; child = child.NextSibling())
            {
                if (visited >= policy.MaxElements) { truncated = true; break; }
                Visit(child, depth + 1);
            }
        }
        Visit(root, 0); return new(nodes, truncated);
    }
}
sealed class UiaEvidenceElement : IProtectedEvidenceElement
{
    readonly AutomationElement element;
    public UiaEvidenceElement(AutomationElement element) { this.element = element; }
    public bool IsPassword => element.Current.IsPassword;
    public int ProcessId => element.Current.ProcessId;
    public int ControlTypeId => element.Current.ControlType.Id;
    public Rectangle Bounds
    {
        get { var b = element.Current.BoundingRectangle; if (b.IsEmpty || double.IsNaN(b.X) || double.IsInfinity(b.X) || b.Width > 100000 || b.Height > 100000) return Rectangle.Empty;
            return new((int)b.X, (int)b.Y, (int)b.Width, (int)b.Height); }
    }
    public bool Enabled => element.Current.IsEnabled;
    public string Name => throw new InvalidOperationException("Forbidden");
    public IProtectedEvidenceElement FirstChild() { var child = TreeWalker.RawViewWalker.GetFirstChild(element); return child == null ? null : new UiaEvidenceElement(child); }
    public IProtectedEvidenceElement NextSibling() { var child = TreeWalker.RawViewWalker.GetNextSibling(element); return child == null ? null : new UiaEvidenceElement(child); }
}
public static class EvidenceWorker
{
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    public static async Task<int> RunAsync()
    {
        // No GUI initialization, hooks, settings, clipboard, audio, or input injection.
        var response = new WorkerReply("capture-unavailable");
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var work = await EvidenceWire.ReadAsync<CaptureWork>(Console.OpenStandardInput(), limit.Token);
            if (work.DeadlineUtc <= DateTimeOffset.UtcNow || work.DeadlineUtc > DateTimeOffset.UtcNow.AddSeconds(30)) throw new EvidenceUnavailableException("deadline-exceeded");
            limit.CancelAfter(work.DeadlineUtc - DateTimeOffset.UtcNow);
            using var validator = new WindowsEvidenceValidator();
            if (!validator.Matches(work.Target)) throw new EvidenceUnavailableException("target-mismatch");
            IntPtr hwnd = new(work.Target.WindowHandle);
            if (N.DwmGetWindowAttribute(hwnd, 9, out N.RECT rect, Marshal.SizeOf<N.RECT>()) != 0) throw new EvidenceUnavailableException("capture-unavailable");
            var area = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            byte[] protectedPng = EvidenceFiles.OpaquePng(area.Size); // Validate area before allocating a raw capture.
            var ui = ProtectedEvidenceUi.Collect(new UiaEvidenceElement(AutomationElement.FromHandle(hwnd)), work.Target.Pid, area, work.Policy, limit.Token);
            using (var raw = new Bitmap(area.Width, area.Height))
            {
                using var graphics = Graphics.FromImage(raw); IntPtr dc = graphics.GetHdc(); bool captured;
                try { captured = PrintWindow(hwnd, dc, 2); } finally { graphics.ReleaseHdc(dc); }
                if (!captured) throw new EvidenceUnavailableException("capture-unavailable");
                // Erase raw pixels in memory before disposal. Only the separately constructed opaque image is serialized.
                graphics.Clear(Color.Black);
            }
            if (!validator.Matches(work.Target)) throw new EvidenceUnavailableException("target-mismatch");
            var capturedAt = DateTimeOffset.UtcNow;
            long offset = work.SessionClockStartTicks > 0 ? (long)Stopwatch.GetElapsedTime(work.SessionClockStartTicks).TotalMilliseconds : (long)(capturedAt - work.SessionStartedUtc).TotalMilliseconds;
            byte[] video = work.Policy.RecordVideo ? await EvidenceVideo.EncodeAsync(work.Policy.FfmpegPath, protectedPng, limit.Token) : null;
            try { EvidenceFiles.Save(work, protectedPng, ui, capturedAt, video, offset); }
            catch (IOException) { throw new EvidenceUnavailableException("save-unavailable"); }
            catch (UnauthorizedAccessException) { throw new EvidenceUnavailableException("save-unavailable"); }
            response = new("saved");
        }
        catch (OperationCanceledException) { response = new("deadline-exceeded"); }
        catch (EvidenceUnavailableException error) { response = new(error.Code); }
        catch { response = new("capture-unavailable"); }
        try { await EvidenceWire.WriteAsync(Console.OpenStandardOutput(), response, CancellationToken.None); } catch { return 1; }
        return response.Status == "saved" ? 0 : 1;
    }
}
public sealed record WorkerReply(string Status);
