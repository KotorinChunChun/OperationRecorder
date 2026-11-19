using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using OpRec;

namespace OperationRecorderApp.ExternalEvidence;

public sealed record PasswordFieldBinding(string FieldId, long FieldHwnd, long LabelHwnd);
public sealed record PasswordMaskEvidence(string SceneId, PasswordFieldBinding[] Fields);
public sealed record PasswordWindowMatch(PasswordEvidenceScene Scene, EvidenceUi Ui, PasswordFieldBinding[] Fields);

/// <summary>Compares a pinned window against registered password-field rules without reading input values.</summary>
public static class PasswordWindowObserver
{
    const uint GaParent = 1, GwOwner = 4;
    const int UiaTextControl = 50020, UiaWindowControl = 50032, MaximumElements = 256, MaximumDepth = 16;

    sealed record ObservedElement(AutomationElement Element, long Hwnd, Rectangle Bounds, EvidenceNode Node);

    /// <summary>For the isolated worker, with application identity already validated by the caller.</summary>
    public static PasswordWindowMatch Match(EvidenceTarget target, WindowObservation window, WindowGraphSnapshot graph,
        PasswordEvidenceProfile profile, string phase, EvidencePolicy policy, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var previousDpi = N.SetThreadDpiAwarenessContext(N.DPI_PER_MONITOR_AWARE_V2);
        if (previousDpi == IntPtr.Zero) return null;
        try
        {
            if (profile?.Scenes == null || policy == null || !MatchesNativeRoot(target, window, graph, out var rootClass))
                return null;

            var candidates = profile.Scenes.Where(scene => MatchesScene(scene, window, rootClass) &&
                scene.Phases?.Contains(phase, StringComparer.Ordinal) == true).ToArray();
            if (candidates.Length == 0) return null;

            var elements = ReadVisibleStructure(target, window, policy, token);
            if (elements == null) return null;
            // A native child that is visible but absent from UIA cannot be covered by the approved shape.
            var visibleHandles = elements.Select(element => element.Hwnd).ToHashSet();
            if (graph.Windows.Any(child => child.Visible && child.IsChild && !visibleHandles.Contains(child.Hwnd)))
                return null;
            var ui = new EvidenceUi(elements.Select(element => element.Node).ToArray(), false);
            var structure = PasswordEvidenceGate.StructureSha256(ui);
            if (structure == null) return null;

            PasswordWindowMatch result = null;
            foreach (var scene in candidates)
            {
                if (!string.Equals(scene.UiSha256, structure, StringComparison.OrdinalIgnoreCase)) continue;
                var bindings = BindFields(scene, target, window, graph, elements, token);
                if (bindings == null) continue;
                if (result != null) return null;
                result = new(scene, ui, bindings);
            }

            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally { N.SetThreadDpiAwarenessContext(previousDpi); }
    }

    /// <summary>Rechecks live native identities and registered bounds in the broker without any UIA calls.</summary>
    public static bool VerifyNative(PasswordEvidenceScene scene, EvidenceTarget target, WindowObservation root,
        WindowGraphSnapshot graph, PasswordFieldBinding[] fields)
    {
        var previousDpi = N.SetThreadDpiAwarenessContext(N.DPI_PER_MONITOR_AWARE_V2);
        if (previousDpi == IntPtr.Zero) return false;
        try
        {
            if (!MatchesNativeRoot(target, root, graph, out var rootClass) || !MatchesScene(scene, root, rootClass) ||
                fields == null || fields.Length != scene.Fields.Length) return false;

            var fieldIds = new HashSet<string>(StringComparer.Ordinal);
            var handles = new HashSet<long>();
            foreach (var binding in fields)
            {
                if (binding == null || !fieldIds.Add(binding.FieldId) ||
                    !handles.Add(binding.FieldHwnd) || !handles.Add(binding.LabelHwnd)) return false;
                var rule = scene.Fields.SingleOrDefault(candidate => candidate.FieldId == binding.FieldId);
                if (rule == null) return false;

                if (!ReadNativeChild(target, root, graph, binding.FieldHwnd, out var fieldBounds, out var fieldClass) ||
                    IsPublicLabelClass(fieldClass) || !MatchesClass(fieldClass, rule.FieldClassPrefix) ||
                    !ReadNativeChild(target, root, graph, binding.LabelHwnd, out var labelBounds, out var labelClass) ||
                    !IsPublicLabelClass(labelClass) || !MatchesClass(labelClass, rule.LabelClassPrefix) ||
                    !MatchesFieldBounds(root, rule, fieldBounds) || !IsAdjacent(labelBounds, fieldBounds, root.Dpi))
                    return false;
            }

            // Reject a root change even if it happened during verification of its children.
            return MatchesNativeRoot(target, root, graph, out var finalClass) && finalClass == rootClass;
        }
        catch { return false; }
        finally { N.SetThreadDpiAwarenessContext(previousDpi); }
    }

    static bool MatchesNativeRoot(EvidenceTarget target, WindowObservation root, WindowGraphSnapshot graph,
        out string className)
    {
        className = null;
        if (target == null || root == null || root.Hwnd == 0 || root.Hwnd != target.WindowHandle || root.Pid != target.Pid ||
            root.Relation != "root" || root.IsChild || root.ParentHwnd != 0 || !root.Visible || root.Minimized || root.Cloaked ||
            root.Generation <= 0 || root.WindowId == Guid.Empty || root.Dpi <= 0 ||
            root.Bounds.Width <= 0 || root.Bounds.Height <= 0 || graph?.Windows == null || graph.Truncated ||
            graph.Windows.Count(window => window == root) != 1 ||
            graph.Windows.Any(window => window == null || window.Pid != target.Pid || window.Relation == "foreign" ||
                (window.Hwnd != root.Hwnd && window.Visible && (!window.IsChild || window.Relation != "child"))))
            return false;

        var hwnd = new IntPtr(root.Hwnd);
        if (!NativePidMatches(hwnd, target.Pid) || !N.IsWindowVisible(hwnd) || !IsWindowEnabled(hwnd) || N.IsIconic(hwnd) ||
            N.GetAncestor(hwnd, N.GA_ROOT) != hwnd || N.GetDpiForWindow(hwnd) != (uint)root.Dpi ||
            GetWindow(hwnd, GwOwner).ToInt64() != root.OwnerHwnd ||
            N.DwmGetWindowAttribute(hwnd, N.DWMWA_CLOAKED, out int cloak, sizeof(int)) != 0 || cloak != 0 ||
            N.DwmGetWindowAttribute(hwnd, N.DWMWA_EXTENDED_FRAME_BOUNDS, out N.RECT frame, Marshal.SizeOf<N.RECT>()) != 0 ||
            !TryRectangle(frame, out var frameBounds) || frameBounds != root.Bounds)
            return false;

        if (root.OwnerHwnd != 0 && !graph.Windows.Any(window => window.Hwnd == root.OwnerHwnd &&
            window.Hwnd != root.Hwnd && !window.IsChild && !window.Visible && NativePidMatches(new IntPtr(window.Hwnd), target.Pid)))
            return false;

        className = ReadClass(hwnd);
        return className != null;
    }

    static bool MatchesScene(PasswordEvidenceScene scene, WindowObservation root, string rootClass) =>
        scene != null && scene.Fields is { Length: > 0 } && scene.Dpi == root.Dpi &&
        scene.WindowWidth == root.Bounds.Width && scene.WindowHeight == root.Bounds.Height &&
        MatchesClass(rootClass, scene.WindowClassPrefix);

    static List<ObservedElement> ReadVisibleStructure(EvidenceTarget target, WindowObservation window,
        EvidencePolicy policy, CancellationToken token)
    {
        int count = 0;
        int elementLimit = Math.Clamp(policy.MaxElements, 1, MaximumElements);
        int depthLimit = Math.Clamp(policy.MaxDepth, 1, MaximumDepth);
        var observed = new List<ObservedElement>();
        var walker = TreeWalker.RawViewWalker;

        bool Visit(AutomationElement element, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > depthLimit || ++count > elementLimit) return false;

            var current = element.Current;
            bool password = current.IsPassword;
            if (current.ProcessId != target.Pid) return false;
            bool offscreen = current.IsOffscreen;
            if (offscreen)
            {
                // Hidden password reports cannot establish that the credential stays inside the approved mask.
                if (password || depth == 0) return false;
            }
            else
            {
                if (!TryRectangle(current.BoundingRectangle, out var bounds)) return false;
                int type = current.ControlType?.Id ?? 0;
                long handle = unchecked((uint)current.NativeWindowHandle);
                if (depth == 0)
                {
                    if (handle != window.Hwnd || type != UiaWindowControl || !bounds.Contains(window.Bounds)) return false;
                    bounds = window.Bounds;
                }
                else if (!window.Bounds.Contains(bounds)) return false;

                var relative = RelativeTo(bounds, window.Bounds);
                observed.Add(new(element, handle, bounds,
                    new(type, relative.X, relative.Y, relative.Width, relative.Height, password, current.IsEnabled)));
            }

            for (var child = walker.GetFirstChild(element); child != null; child = walker.GetNextSibling(child))
                if (!Visit(child, depth + 1)) return false;
            return true;
        }

        var root = AutomationElement.FromHandle(new IntPtr(window.Hwnd));
        return root != null && Visit(root, 0) ? observed : null;
    }

    static PasswordFieldBinding[] BindFields(PasswordEvidenceScene scene, EvidenceTarget target, WindowObservation root,
        WindowGraphSnapshot graph, IReadOnlyList<ObservedElement> elements, CancellationToken token)
    {
        var bindings = new List<PasswordFieldBinding>();
        var handles = new HashSet<long>();
        foreach (var rule in scene.Fields.OrderBy(rule => rule.FieldId, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            ObservedElement field = null;
            foreach (var candidate in elements)
            {
                if (candidate.Node.ControlTypeId != rule.FieldControlTypeId || !MatchesFieldBounds(root, rule, candidate.Bounds) ||
                    !MatchedNativeChild(candidate, target, root, graph, out var className) ||
                    IsPublicLabelClass(className) || !MatchesClass(className, rule.FieldClassPrefix)) continue;
                if (field != null) return null;
                field = candidate;
            }
            if (field == null || !handles.Add(field.Hwnd)) return null;

            ObservedElement label = null;
            foreach (var candidate in elements)
            {
                if (candidate.Node.IsPassword || candidate.Node.ControlTypeId != UiaTextControl ||
                    !IsAdjacent(candidate.Bounds, field.Bounds, root.Dpi) ||
                    !MatchedNativeChild(candidate, target, root, graph, out var className) ||
                    !IsPublicLabelClass(className) || !MatchesClass(className, rule.LabelClassPrefix)) continue;

                token.ThrowIfCancellationRequested();
                // Only a separately verified public STATIC label is allowed to supply Name.
                // Never read the field's Name, any Value, or return any observed caption.
                if (!string.Equals(candidate.Element.Current.Name, rule.PublicLabel, StringComparison.Ordinal)) continue;
                if (label != null) return null;
                label = candidate;
            }
            if (label == null || !handles.Add(label.Hwnd)) return null;
            bindings.Add(new(rule.FieldId, field.Hwnd, label.Hwnd));
        }

        if (elements.Any(element => element.Node.IsPassword && !bindings.Any(binding => binding.FieldHwnd == element.Hwnd)))
            return null;

        var confirmed = bindings.ToArray();
        return VerifyNative(scene, target, root, graph, confirmed) ? confirmed : null;
    }

    static bool MatchedNativeChild(ObservedElement candidate, EvidenceTarget target, WindowObservation root,
        WindowGraphSnapshot graph, out string className)
    {
        className = null;
        return candidate.Node.Enabled && ReadNativeChild(target, root, graph, candidate.Hwnd, out var native, out className) &&
            native == candidate.Bounds;
    }

    static bool ReadNativeChild(EvidenceTarget target, WindowObservation root, WindowGraphSnapshot graph, long handle,
        out Rectangle bounds, out string className)
    {
        bounds = Rectangle.Empty;
        className = null;
        if (handle == 0 || handle == root.Hwnd) return false;
        var child = new IntPtr(handle);
        var parent = new IntPtr(root.Hwnd);
        var observed = graph.Windows.SingleOrDefault(window => window.Hwnd == handle);
        if (observed == null || observed.Pid != target.Pid || observed.Generation <= 0 || observed.WindowId == Guid.Empty ||
            observed.Relation != "child" || !observed.IsChild || !observed.Visible || observed.OwnerHwnd != 0 ||
            observed.Dpi != root.Dpi || !NativePidMatches(child, target.Pid) || !IsChild(parent, child) ||
            N.GetAncestor(child, N.GA_ROOT) != parent || N.GetAncestor(child, GaParent).ToInt64() != observed.ParentHwnd ||
            GetWindow(child, GwOwner) != IntPtr.Zero || N.GetDpiForWindow(child) != (uint)root.Dpi ||
            !N.IsWindowVisible(child) || !IsWindowEnabled(child) ||
            !N.GetWindowRect(child, out var rect) || !TryRectangle(rect, out bounds) ||
            bounds != observed.Bounds || !root.Bounds.Contains(bounds)) return false;

        className = ReadClass(child);
        return className != null;
    }

    static bool MatchesFieldBounds(WindowObservation root, PasswordFieldRule rule, Rectangle bounds)
    {
        if (!root.Bounds.Contains(bounds)) return false;
        var relative = RelativeTo(bounds, root.Bounds);
        return relative == rule.FieldBounds && rule.Mask.Contains(relative) &&
            new Rectangle(Point.Empty, root.Bounds.Size).Contains(rule.Mask);
    }

    static bool IsAdjacent(Rectangle label, Rectangle field, int dpi)
    {
        long gap = (long)field.Left - label.Right;
        long twiceCenterDifference = Math.Abs((long)field.Top + field.Bottom - (long)label.Top - label.Bottom);
        // The thresholds are expressed at 96 DPI, the same dimensions as the login runner's selector.
        return gap >= 0 && gap * 96 <= (long)dpi * 40 && twiceCenterDifference * 96 <= (long)dpi * 6 &&
            (long)field.Width * 96 >= (long)dpi * 80 && (long)field.Height * 96 >= (long)dpi * 16 &&
            (long)field.Height * 96 <= (long)dpi * 60;
    }

    static Rectangle RelativeTo(Rectangle bounds, Rectangle root) =>
        new(bounds.X - root.X, bounds.Y - root.Y, bounds.Width, bounds.Height);

    static bool TryRectangle(N.RECT native, out Rectangle rectangle) =>
        TryRectangle(native.Left, native.Top, native.Right, native.Bottom, out rectangle);

    static bool TryRectangle(System.Windows.Rect automation, out Rectangle rectangle) =>
        TryRectangle(automation.Left, automation.Top, automation.Right, automation.Bottom, out rectangle);

    static bool TryRectangle(double left, double top, double right, double bottom, out Rectangle rectangle)
    {
        rectangle = Rectangle.Empty;
        if (!WholeInt(left) || !WholeInt(top) || !WholeInt(right) || !WholeInt(bottom) ||
            right - left is <= 0 or > 16384 || bottom - top is <= 0 or > 16384) return false;
        rectangle = Rectangle.FromLTRB((int)left, (int)top, (int)right, (int)bottom);
        return true;
    }

    static bool WholeInt(double value) => value is >= int.MinValue and <= int.MaxValue && value == Math.Truncate(value);
    static bool NativePidMatches(IntPtr hwnd, int pid) => pid > 0 && N.IsWindow(hwnd) &&
        N.GetWindowThreadProcessId(hwnd, out uint actual) != 0 && actual == (uint)pid;

    static string ReadClass(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        int length = N.GetClassName(hwnd, buffer, buffer.Capacity);
        return length is > 0 and < 255 ? buffer.ToString() : null;
    }

    static bool IsPublicLabelClass(string actual) => string.Equals(actual, "Static", StringComparison.OrdinalIgnoreCase) ||
        MatchesClass(actual, "WindowsForms10.STATIC.");

    static bool MatchesClass(string actual, string prefix) => actual != null && !string.IsNullOrEmpty(prefix) &&
        actual.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    [DllImport("user32.dll")] static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
}
