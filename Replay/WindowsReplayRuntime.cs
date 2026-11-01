using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using OpRec;

namespace OperationRecorderApp.Replay;

// 入力の送信先はconfigで特定したウィンドウだけ。画像照合もこの範囲だけを取得する。
public sealed class WindowsReplayRuntime : IReplayRuntime
{
    readonly ReplayConfig config;
    volatile bool started;
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
    public long ElapsedMilliseconds => Environment.TickCount64;
    sealed record Match(IntPtr Window, int Pid, Rectangle Frame, Rectangle Bounds, AutomationElement Element, ReplayElement Selector);
    public WindowsReplayRuntime(ReplayConfig config) { this.config = config; }
    public void EndRun() { started = false; }
    public async Task Delay(int milliseconds, CancellationToken cancellation)
    {
        for (int remaining = milliseconds; remaining > 0; remaining -= 50)
        { CheckDesktop(); CheckForeground(); await Task.Delay(Math.Min(remaining, 50), cancellation); }
        cancellation.ThrowIfCancellationRequested();
    }
    public async Task<ReplayObservation> Observe(ReplayConfig c, ReplayStep step, ReplayImage image, bool after, CancellationToken cancellation)
    {
        CheckDesktop(); CheckForeground();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        request.CancelAfter(TimeSpan.FromSeconds(3));
        // UIA providerが停止していても新しい探索workerを無制限に積まず、その回で中止する。
        try { return await Task.Run(() => Resolve(c, step, image, after, request.Token), request.Token).WaitAsync(TimeSpan.FromSeconds(3), cancellation); }
        catch (TimeoutException) { throw new TimeoutException("UIA提供元が応答しません。操作は再試行していません。"); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new TimeoutException("UIA提供元が応答しません。"); }
        finally { request.Cancel(); }
    }
    ReplayObservation Resolve(ReplayConfig c, ReplayStep step, ReplayImage image, bool after, CancellationToken cancellation)
    {
        using var dpi = new DpiContext();
        cancellation.ThrowIfCancellationRequested();
        var target = c.Targets[step.Target];
        var windows = Windows(target).ToArray();
        if (windows.Length == 0) return Waiting("waitingForWindow", "ウィンドウを探しています。");
        windows = windows.Where(h => target.MatchesClass(WinUtil.ClassOf(h))).ToArray();
        if (windows.Length == 0) return Waiting("waitingForClass", "クラスを探しています。");
        windows = windows.Where(h => target.MatchesTitle(WinUtil.TextOf(h))).ToArray();
        if (windows.Length == 0) return Waiting("waitingForWindow", "指定タイトルのウィンドウを探しています。");
        windows = windows.Where(h => target.MatchesWindow(WinUtil.TextOf(h), WinUtil.ClassOf(h))).ToArray();
        if (windows.Length == 0) return Waiting("waitingForCondition", "ウィンドウの条件式が一致するのを待っています。");
        if (windows.Length != 1) throw new InvalidOperationException("ウィンドウ候補が複数あります。bindingを絞ってください。");
        cancellation.ThrowIfCancellationRequested();
        IntPtr window = windows[0]; int pid = checked((int)WinUtil.PidOf(window));
        if (!IsWindowEnabled(window) || IsIconic(window)) return Waiting("waitingForWindow", "操作可能なウィンドウを待っています（モーダル/最小化）。");
        Rectangle frame = WinUtil.GetFrame(window);
        if (frame.Width <= 0 || frame.Height <= 0 || !WinUtil.VirtualScreen().Contains(frame)) return Waiting("waitingForWindow", "画面内のウィンドウを待っています。");
        bool needsForeground = image != null || step.Kind != "wait";
        if (needsForeground && GetForegroundWindow() != window)
        {
            CheckForeground(); Activate(window);
            if (GetForegroundWindow() != window) return Waiting("waitingForWindow", "対象が前面になるのを待っています。");
        }
        AutomationElement element = null; Rectangle bounds = frame;
        if (!after && step.PositionMode == "element" && step.Element != null && step.Kind != "wait")
        {
            var selector = step.Element;
            var root = AutomationElement.FromHandle(window);
            Condition condition = !string.IsNullOrWhiteSpace(selector.AutomationId)
                ? new PropertyCondition(AutomationElement.AutomationIdProperty, selector.AutomationId)
                : !string.IsNullOrEmpty(selector.Name) ? new PropertyCondition(AutomationElement.NameProperty, selector.Name) : Condition.TrueCondition;
            var matches = root.FindAll(TreeScope.Subtree, condition).Cast<AutomationElement>()
                .Where(e => selector.Matches(e.Current.AutomationId, e.Current.Name, e.Current.ClassName, e.Current.ControlType.ProgrammaticName)).Take(10001).ToArray();
            if (matches.Length == 0) return Waiting("waitingForElement", "該当するUI要素を探しています。");
            if (matches.Length != 1) throw new InvalidOperationException("UI要素の候補が複数あります。AutomationId/名前/クラスを絞ってください。");
            element = matches[0]; var value = element.Current;
            if (!value.IsEnabled || value.IsOffscreen) return Waiting("waitingForElement", "UI要素が操作可能になるのを待っています。");
            bounds = value.NativeWindowHandle == window.ToInt64() ? frame : RectangleOf(value.BoundingRectangle);
            if (bounds.Width <= 0 || bounds.Height <= 0 || !frame.Contains(bounds)) return Waiting("waitingForElement", "UI要素がウィンドウ内に現れるのを待っています。");
        }
        if (!after && step.PositionMode == "window" && step.Kind != "wait" && !ReplayGeometry.Compatible(target, frame, checked((int)GetDpiForWindow(window))))
            throw new InvalidOperationException("相対座標の縦横比/DPI条件が変わっています。UIAでの再特定かconfig補正が必要です。");
        if (image != null)
        {
            cancellation.ThrowIfCancellationRequested();
            // 前面が変わった場合に別アプリの画面を画像待ちの証跡へ取り込まない。
            if (GetForegroundWindow() != window) throw new InvalidOperationException("画像照合中に前面が変わりました。");
            using var expected = new Bitmap(c.PathOf(image.File));
            using var current = new Bitmap(frame.Width, frame.Height);
            using (var graphics = Graphics.FromImage(current)) graphics.CopyFromScreen(frame.Location, Point.Empty, frame.Size);
            var crop = image.Crop == null ? new Rectangle(Point.Empty, expected.Size) : new Rectangle(image.Crop.X, image.Crop.Y, image.Crop.W, image.Crop.H);
            double similarity = ReplayGeometry.Similarity(expected, crop, current);
            if (similarity < image.Threshold) return Waiting("waitingForImage", (after ? "操作後の画像が一致するのを待っています。" : "該当画像になるのを待っています。") + $"（一致度 {similarity:F3} / 条件 {image.Threshold:F3}）");
        }
        cancellation.ThrowIfCancellationRequested();
        return new() { Ready = true, State = "ready", Message = "実行条件が揃いました。", Context = new Match(window, pid, frame, bounds, element, step.Element) };
    }
    public async Task Execute(ReplayConfig c, ReplayStep step, ReplayObservation observation, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); CheckDesktop(); CheckForeground();
        if (step.Kind == "wait") return;
        if (step.Kind == "launch")
        {
            var start = new ProcessStartInfo(c.Targets[step.Target].Executable) { UseShellExecute = false };
            foreach (var argument in step.Arguments ?? Array.Empty<string>()) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("アプリを起動できません。"); return;
        }
        var match = observation.Context as Match ?? throw new InvalidOperationException("照合結果がありません。");
        Verify(match, c.Targets[step.Target]);
        foreach (int key in new[] { 1, 2, 4, 16, 17, 18, 91, 92 })
            if ((GetAsyncKeyState(key) & 0x8000) != 0) throw new InvalidOperationException("利用者のキーまたはマウスボタンが押されています。");
        Point point = ReplayGeometry.Position(match.Bounds, step.X, step.Y);
        started = true;
        if (step.Kind == "input")
        {
            string value = string.IsNullOrWhiteSpace(step.Text.Environment) ? step.Text.Literal : Environment.GetEnvironmentVariable(step.Text.Environment);
            if (value == null) throw new InvalidOperationException("入力値が未設定です。");
            var element = match.Element ?? throw new InvalidOperationException("入力欄のUIAが必要です。");
            if (element.Current.IsPassword && string.IsNullOrWhiteSpace(step.Text.Environment)) throw new InvalidOperationException("実際のpassword欄には環境変数参照が必要です。");
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || ((ValuePattern)pattern).Current.IsReadOnly) throw new InvalidOperationException("入力欄はValuePatternで編集できません。");
            // 値やUIA例外本文はログへ渡さない。
            using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellation); request.CancelAfter(TimeSpan.FromSeconds(3));
            try { await Task.Run(() =>
                {
                    Verify(match, c.Targets[step.Target]); request.Token.ThrowIfCancellationRequested(); var field = (ValuePattern)pattern; field.SetValue(value);
                    if (!element.Current.IsPassword && field.Current.Value != value) throw new InvalidOperationException();
                }, request.Token).WaitAsync(TimeSpan.FromSeconds(3), cancellation); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception) { throw new InvalidOperationException("入力欄への値の設定を完了できませんでした。"); }
            finally { request.Cancel(); }
            return;
        }
        if (step.Kind == "key")
        {
            if (match.Element != null) match.Element.SetFocus();
            var keys = ReplayKeys.Parse(step.Key);
            Send(match, c.Targets[step.Target], keys.Select(k => Keyboard(k)).Concat(keys.Reverse().Select(k => Keyboard(k, 2))).ToArray()); return;
        }
        VerifyPoint(match, point);
        Send(match, c.Targets[step.Target], Move(point));
        uint down = step.Kind == "rightclick" || step.Kind == "rightdrag" ? 8u : step.Kind == "middleclick" ? 32u : 2u;
        if (step.Kind == "wheel") { Send(match, c.Targets[step.Target], Mouse(0x800, unchecked((uint)(step.WheelNotches * 120)))); return; }
        if (step.Kind == "drag" || step.Kind == "rightdrag")
        {
            Point end = ReplayGeometry.Position(match.Bounds, step.EndX, step.EndY); VerifyPoint(match, end);
            Send(match, c.Targets[step.Target], Mouse(down));
            try
            {
                for (int i = 1; i <= 8; i++)
                { cancellation.ThrowIfCancellationRequested(); var next = new Point(point.X + (end.X - point.X) * i / 8, point.Y + (end.Y - point.Y) * i / 8); VerifyPoint(match, next); Send(match, c.Targets[step.Target], Move(next)); await Delay(20, cancellation); }
            }
            finally { SendInput(1, new[] { Mouse(down << 1) }, Marshal.SizeOf<INPUT>()); }
            return;
        }
        Send(match, c.Targets[step.Target], Mouse(down), Mouse(down << 1));
        if (step.Kind == "doubleclick") { await Delay(60, cancellation); cancellation.ThrowIfCancellationRequested(); Send(match, c.Targets[step.Target], Mouse(down), Mouse(down << 1)); }
    }
    public string ApplicationIdentity(ReplayTarget target)
    {
        return string.Join(",", Windows(target).Where(h => IsWindowEnabled(h) && !IsIconic(h) && target.MatchesWindow(WinUtil.TextOf(h), WinUtil.ClassOf(h))).Select(h =>
        {
            using var process = Process.GetProcessById(checked((int)WinUtil.PidOf(h)));
            return process.Id + ":" + process.StartTime.ToUniversalTime().Ticks;
        }).Distinct().OrderBy(x => x));
    }
    static ReplayObservation Waiting(string state, string message) => new() { State = state, Message = message };
    static Rectangle RectangleOf(System.Windows.Rect value) => value.IsEmpty || !double.IsFinite(value.X) ? Rectangle.Empty : new((int)value.X, (int)value.Y, (int)value.Width, (int)value.Height);
    static List<IntPtr> Windows(ReplayTarget target)
    {
        var result = new List<IntPtr>(); int session = Process.GetCurrentProcess().SessionId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            try
            {
                int pid = checked((int)WinUtil.PidOf(h)); if (pid == Environment.ProcessId || target.ProcessId > 0 && target.ProcessId != pid) return true;
                using var p = Process.GetProcessById(pid);
                if (p.SessionId == session && string.Equals(p.MainModule?.FileName, Path.GetFullPath(target.Executable), StringComparison.OrdinalIgnoreCase)) result.Add(h);
            }
            catch (Exception) { }
            return true;
        }, IntPtr.Zero); return result;
    }
    void CheckForeground()
    {
        if (!started) return;
        int pid = checked((int)WinUtil.PidOf(GetForegroundWindow()));
        try
        {
            using var process = Process.GetProcessById(pid);
            IntPtr foreground = GetForegroundWindow();
            if (config.Targets.Values.Any(t => t != null && !string.IsNullOrEmpty(t.Executable) && string.Equals(Path.GetFullPath(t.Executable), process.MainModule?.FileName, StringComparison.OrdinalIgnoreCase) &&
                (t.ProcessId == 0 || t.ProcessId == pid) && t.MatchesWindow(WinUtil.TextOf(foreground), WinUtil.ClassOf(foreground)))) return;
        }
        catch (Exception) { }
        throw new InvalidOperationException("前面が対象アプリから離れたため停止しました。");
    }
    static void Verify(Match match, ReplayTarget target)
    {
        using var dpi = new DpiContext();
        if (!IsWindow(match.Window) || GetForegroundWindow() != match.Window || WinUtil.PidOf(match.Window) != match.Pid || WinUtil.GetFrame(match.Window) != match.Frame || !IsWindowEnabled(match.Window) || IsIconic(match.Window)) throw new InvalidOperationException("実行直前に対象ウィンドウの状態が変わりました。");
        using var p = Process.GetProcessById(match.Pid);
        if (!string.Equals(p.MainModule?.FileName, Path.GetFullPath(target.Executable), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("対象EXEが変わりました。");
        if (!target.MatchesWindow(WinUtil.TextOf(match.Window), WinUtil.ClassOf(match.Window))) throw new InvalidOperationException("実行直前にウィンドウ条件が変わりました。");
        if (match.Element != null && (!match.Element.Current.IsEnabled || match.Element.Current.IsOffscreen ||
            !match.Selector.Matches(match.Element.Current.AutomationId, match.Element.Current.Name, match.Element.Current.ClassName, match.Element.Current.ControlType.ProgrammaticName) ||
            (match.Element.Current.NativeWindowHandle == match.Window.ToInt64() ? match.Frame : RectangleOf(match.Element.Current.BoundingRectangle)) != match.Bounds)) throw new InvalidOperationException("実行直前にUI要素の状態が変わりました。");
    }
    static void VerifyPoint(Match match, Point point)
    {
        using var dpi = new DpiContext();
        if (!match.Frame.Contains(point) || WinUtil.Root(WindowFromPoint(point)) != match.Window) throw new InvalidOperationException("操作位置を別のウィンドウが覆っています。");
    }
    public static void CheckDesktop()
    {
        if (!Environment.UserInteractive) throw new InvalidOperationException("対話デスクトップがありません。");
        var desktop = OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) throw new InvalidOperationException("入力デスクトップにアクセスできません（ロック/権限差）。");
        try
        {
            var name = new StringBuilder(256);
            if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) || !string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("ロック/セキュアデスクトップでは実行できません。");
        }
        finally { CloseDesktop(desktop); }
    }
    static void Activate(IntPtr window)
    {
        uint other = GetWindowThreadProcessId(GetForegroundWindow(), out _), current = GetCurrentThreadId();
        bool attached = other != 0 && other != current && AttachThreadInput(current, other, true);
        try { SetForegroundWindow(window); } finally { if (attached) AttachThreadInput(current, other, false); }
    }
    static void Send(Match match, ReplayTarget target, params INPUT[] inputs)
    {
        Verify(match, target);
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            // 途中失敗で、この処理自身が押した修飾キーやボタンを保持しない。
            var releases = inputs.Take((int)sent).Where(i => i.Type == 1 && (i.Union.Keyboard.Flags & 2) == 0)
                .Select(i => Keyboard(i.Union.Keyboard.Key, 2)).ToArray();
            if (releases.Length > 0) SendInput((uint)releases.Length, releases, Marshal.SizeOf<INPUT>());
            throw new InvalidOperationException("入力を送信できません（UIPI/権限差の可能性）。再試行していません。");
        }
    }
    static INPUT Move(Point point)
    {
        using var dpi = new DpiContext();
        Rectangle screen = WinUtil.VirtualScreen();
        return new() { Union = new() { Mouse = new() { X = (int)Math.Round((point.X - screen.X) * 65535d / Math.Max(1, screen.Width - 1)), Y = (int)Math.Round((point.Y - screen.Y) * 65535d / Math.Max(1, screen.Height - 1)), Flags = 0xC001 } } };
    }
    static INPUT Mouse(uint flag, uint data = 0) => new() { Union = new() { Mouse = new() { Flags = flag, Data = data } } };
    static INPUT Keyboard(int key, uint flags = 0) => new() { Type = 1, Union = new() { Keyboard = new() { Key = (ushort)key, Flags = flags } } };
    sealed class DpiContext : IDisposable
    {
        readonly IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        public void Dispose() { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint Type; public UNION Union; }
    [StructLayout(LayoutKind.Explicit)] struct UNION { [FieldOffset(0)] public MOUSE Mouse; [FieldOffset(0)] public KEY Keyboard; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSE { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEY { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    delegate bool EnumProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder buffer, int length, out int needed);
}
