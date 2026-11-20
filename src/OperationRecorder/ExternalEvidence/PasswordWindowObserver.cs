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

internal sealed record PasswordLayoutStructureDiagnostic(string Code, bool? Win32RootEnabled, bool? UiaRootEnabled,
    int VisitedElements, int DeepestDepth, int MaxElements, int MaxDepth);

/// <summary>Compares a pinned window against registered password-field rules without reading input values.</summary>
public static class PasswordWindowObserver
{
    const uint GaParent = 1, GwOwner = 4;
    const int UiaTextControl = 50020, UiaWindowControl = 50032, MaximumElements = 256, MaximumDepth = 16;

    sealed record ObservedElement(AutomationElement Element, long Hwnd, Rectangle Bounds, EvidenceNode Node);

    internal sealed record LayoutObservation(string WindowClass, string UiSha256, long FieldHwnd, long LabelHwnd,
        PasswordLayoutCandidateField Field);

    internal sealed record BackdropObservation(PasswordLayoutDiagnostics Diagnostics, LayoutObservation Layout = null);

    sealed class StructureTrace(bool win32RootEnabled, int maxElements, int maxDepth)
    {
        string failure;
        internal int VisitedElements { get; set; }
        internal int DeepestDepth { get; set; } = -1;
        internal bool? UiaRootEnabled { get; set; }

        internal bool Fail(string code)
        {
            failure = code;
            return false;
        }

        internal PasswordLayoutStructureDiagnostic Describe(string structureHash)
        {
            string code = failure ?? (structureHash == null ? "uia-structure-hash-unavailable" :
                UiaRootEnabled != win32RootEnabled ? "uia-root-enabled-mismatch" : "ok");
            return new(code, win32RootEnabled, UiaRootEnabled, VisitedElements, DeepestDepth, maxElements, maxDepth);
        }
    }

    internal static LayoutObservation ObserveLayout(PasswordLayoutCandidateRequest request, WindowObservation window,
        WindowGraphSnapshot graph, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var previousDpi = N.SetThreadDpiAwarenessContext(N.DPI_PER_MONITOR_AWARE_V2);
        if (previousDpi == IntPtr.Zero) return null;
        try
        {
            var target = request.Target;
            if (!MatchesNativeRoot(target, window, graph, out var rootClass) ||
                !PasswordLayoutCandidate.IsNativeClass(rootClass)) return null;
            var elements = ReadVisibleStructure(target, window,
                new EvidencePolicy(MaxElements: MaximumElements, MaxDepth: MaximumDepth), token);
            if (!CoversVisibleChildren(graph, elements)) return null;
            var structure = PasswordEvidenceGate.StructureSha256(new(elements.Select(element => element.Node).ToArray(), false));
            if (structure == null) return null;
            return BindCandidateField(request, window, graph, elements, rootClass, structure, token);
        }
        finally { N.SetThreadDpiAwarenessContext(previousDpi); }
    }

    internal static BackdropObservation ObserveBackdrop(PasswordLayoutCandidateRequest request, WindowGraphSnapshot graph,
        string appSha256, string appFileVersion, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var previousDpi = N.SetThreadDpiAwarenessContext(N.DPI_PER_MONITOR_AWARE_V2);
        if (previousDpi == IntPtr.Zero) return new(new("window-layout-unavailable"));
        try
        {
            var target = request.Target;
            var (login, background, selectionReason) = PasswordLayoutBackdrop.Select(target, graph);
            var topology = ReadBackdropTopology(target, login, background, selectionReason);
            if (topology.Reason != null) return new(topology);

            var publicStructurePolicy = new EvidencePolicy(MaxElements: MaximumElements, MaxDepth: MaximumDepth);
            var loginTrace = request.DiagnoseWindowLayout ? new StructureTrace(topology.Login.Enabled, MaximumElements, MaximumDepth) : null;
            var backgroundTrace = request.DiagnoseWindowLayout ? new StructureTrace(topology.Background.Enabled, MaximumElements, MaximumDepth) : null;
            var loginElements = ReadBackdropStructure(target, login, background, publicStructurePolicy, token, loginTrace);
            var backgroundElements = ReadBackdropStructure(target, background, login, publicStructurePolicy, token, backgroundTrace);
            string loginHash = StructureHash(loginElements);
            string backgroundHash = StructureHash(backgroundElements);
            var afterStructure = ReadBackdropTopology(target, login, background, selectionReason);
            if (topology != afterStructure) return NativeChange(request, topology, afterStructure);

            var observed = topology with
            {
                Login = topology.Login with { UiSha256 = loginHash },
                Background = topology.Background with { UiSha256 = backgroundHash },
                LoginStructure = loginTrace?.Describe(loginHash),
                BackgroundStructure = backgroundTrace?.Describe(backgroundHash)
            };
            if (loginHash == null || !loginElements[0].Node.Enabled)
                return new(observed with { Reason = "login-structure-unproven",
                    FailureRole = request.DiagnoseWindowLayout ? "login" : null });
            if (backgroundHash == null)
                return new(observed with { Reason = "background-structure-unproven",
                    FailureRole = request.DiagnoseWindowLayout ? "background" : null });
            if (!CoversBackdropChildren(graph, login, background, loginElements, backgroundElements, out var uncovered))
                return new(observed with
                {
                    Reason = "window-layout-unavailable",
                    FailureRole = request.DiagnoseWindowLayout ? uncovered : null,
                    LoginStructure = uncovered == "background" || observed.LoginStructure == null ? observed.LoginStructure :
                        observed.LoginStructure with { Code = "uia-native-children-unproven" },
                    BackgroundStructure = uncovered == "login" || observed.BackgroundStructure == null ? observed.BackgroundStructure :
                        observed.BackgroundStructure with { Code = "uia-native-children-unproven" }
                });

            // The known WinForms peer can report the managed Enabled state after native EnableWindow.
            // Topology has required and rechecked Win32-disabled. Keep the actual UIA value in the
            // structural hash and treat authentication-shaped controls as unproven even if the peer disables them.
            bool backgroundEnabledMismatch = backgroundElements[0].Node.Enabled;
            if (HasBackgroundAuthentication(backgroundElements, background, backgroundEnabledMismatch))
                return new(observed with { Reason = "background-authentication-ui",
                    FailureRole = request.DiagnoseWindowLayout ? "background" : null });

            string layoutSha256 = PasswordLayoutBackdrop.LayoutHash(appSha256, appFileVersion, observed.Ownership,
                observed.Login, observed.Background);
            var diagnostic = observed with { Reason = "review-required", LayoutSha256 = layoutSha256 };
            if (!string.Equals(request.ReviewedWindowLayoutSha256, layoutSha256, StringComparison.OrdinalIgnoreCase))
                return new(diagnostic);

            var layout = BindCandidateField(request, login, graph, loginElements, observed.Login.WindowClass, loginHash, token);
            var afterField = ReadBackdropTopology(target, login, background, selectionReason);
            return topology == afterField ? new(diagnostic, layout) : NativeChange(request, topology, afterField);
        }
        finally { N.SetThreadDpiAwarenessContext(previousDpi); }
    }

    static BackdropObservation NativeChange(PasswordLayoutCandidateRequest request, PasswordLayoutDiagnostics first,
        PasswordLayoutDiagnostics next) => new(new("window-layout-changed", Change: request.DiagnoseWindowLayout
            ? new("native-recheck", PasswordLayoutBackdrop.ObservationDifferences(first, next)) : null));

    static PasswordLayoutDiagnostics ReadBackdropTopology(EvidenceTarget target, WindowObservation login,
        WindowObservation background, string selectionReason)
    {
        var loginState = ReadBackdropWindow(login, target.Pid);
        if (selectionReason != null) return new(selectionReason, Login: loginState);
        var backgroundState = ReadBackdropWindow(background, target.Pid);
        if (loginState == null || backgroundState == null) return new("window-layout-unavailable", Login: loginState, Background: backgroundState);
        string ownership = PasswordLayoutBackdrop.GetOwnership(loginState.Hwnd, loginState.OwnerHwnd,
            backgroundState.Hwnd, backgroundState.OwnerHwnd);
        string reason = null;
        if (!loginState.Enabled) reason = "login-window-disabled";
        else if (backgroundState.Enabled) reason = "background-window-enabled";
        else if (!MatchesClass(loginState.WindowClass, "WindowsForms10.Window.8.") ||
            !string.Equals(loginState.WindowClass, backgroundState.WindowClass, StringComparison.Ordinal))
            reason = "background-window-class-mismatch";
        else if (ownership == null) reason = "unsupported-ownership";
        return new(reason, ownership, Login: loginState, Background: backgroundState);
    }

    static PasswordLayoutWindow ReadBackdropWindow(WindowObservation window, int pid)
    {
        const long ChildStyle = 0x40000000;
        if (window == null || !MatchesNativeTopLevel(window, pid, out bool enabled)) return null;
        var handle = new IntPtr(window.Hwnd);
        if ((N.GetWindowLongPtr(handle, N.GWL_STYLE).ToInt64() & ChildStyle) != 0) return null;
        string className = ReadClass(handle);
        return PasswordLayoutCandidate.IsNativeClass(className)
            ? new(window.Hwnd, window.OwnerHwnd, className, window.Visible, enabled, window.Dpi, window.Bounds) : null;
    }

    static List<ObservedElement> ReadBackdropStructure(EvidenceTarget target, WindowObservation window,
        WindowObservation peer, EvidencePolicy policy, CancellationToken token, StructureTrace trace)
    {
        try { return ReadVisibleStructure(target, window, policy, token, peer.Hwnd, trace); }
        catch (Exception error) when (IsUiaObservationError(error))
        {
            trace?.Fail("uia-exception");
            return null;
        }
    }

    static bool IsUiaObservationError(Exception error) =>
        error is COMException or ElementNotAvailableException or InvalidOperationException or ArgumentException;

    static string StructureHash(List<ObservedElement> elements) => elements == null ? null :
        PasswordEvidenceGate.StructureSha256(new(elements.Select(element => element.Node).ToArray(), false));

    static bool CoversBackdropChildren(WindowGraphSnapshot graph, WindowObservation login, WindowObservation background,
        List<ObservedElement> loginElements, List<ObservedElement> backgroundElements, out string failureRole)
    {
        failureRole = null;
        var loginHandles = loginElements.Select(element => element.Hwnd).ToHashSet();
        var backgroundHandles = backgroundElements.Select(element => element.Hwnd).ToHashSet();
        foreach (var child in graph.Windows.Where(window => window.IsChild && window.Visible))
        {
            var handle = new IntPtr(child.Hwnd);
            if (!NativePidMatches(handle, login.Pid) || !N.IsWindowVisible(handle)) return false;
            long root = N.GetAncestor(handle, N.GA_ROOT).ToInt64();
            if (root == login.Hwnd && loginHandles.Contains(child.Hwnd)) continue;
            if (root == background.Hwnd && backgroundHandles.Contains(child.Hwnd)) continue;
            failureRole = root == login.Hwnd ? "login" : root == background.Hwnd ? "background" : null;
            return false;
        }
        return true;
    }

    static bool HasBackgroundAuthentication(List<ObservedElement> elements, WindowObservation background, bool enabledMismatch) => elements.Any(element =>
        element.Node.IsPassword || ((enabledMismatch || element.Node.Enabled) && (element.Node.ControlTypeId is 50004 or 50025 ||
            element.Node.ControlTypeId == 50033 && NativePidMatches(new IntPtr(element.Hwnd), background.Pid) &&
            MatchesClass(ReadClass(new IntPtr(element.Hwnd)), "WindowsForms10.Window.") &&
            elements.Any(label => label.Node.ControlTypeId == UiaTextControl &&
                IsAdjacent(label.Bounds, element.Bounds, background.Dpi)))));

    static LayoutObservation BindCandidateField(PasswordLayoutCandidateRequest request, WindowObservation window,
        WindowGraphSnapshot graph, List<ObservedElement> elements, string rootClass, string structure, CancellationToken token)
    {
        var target = request.Target;
        var approvedName = new PropertyCondition(AutomationElement.NameProperty, request.PublicLabel);
        LayoutObservation result = null;
        foreach (var label in elements)
        {
            token.ThrowIfCancellationRequested();
            if (label.Node.IsPassword || label.Node.ControlTypeId != UiaTextControl ||
                !MatchedNativeChild(label, target, window, graph, out var labelClass) ||
                !IsPublicLabelClass(labelClass) || !PasswordLayoutCandidate.IsNativeClass(labelClass)) continue;

            // Ask the provider to compare the known label. Do not retrieve captions from nonmatching elements.
            if (label.Element.FindFirst(TreeScope.Element, approvedName) == null) continue;
            if (result != null) return null;
            foreach (var field in elements)
            {
                token.ThrowIfCancellationRequested();
                if (!IsAdjacent(label.Bounds, field.Bounds, window.Dpi) ||
                    !MatchedNativeChild(field, target, window, graph, out var fieldClass) ||
                    !PasswordLayoutCandidate.IsNativeClass(fieldClass) ||
                    !PasswordLayoutCandidate.MatchesField(request.FieldClassPrefix, fieldClass, field.Node)) continue;
                if (result != null) return null;
                result = new(rootClass, structure, field.Hwnd, label.Hwnd,
                    new(fieldClass, labelClass, field.Node.ControlTypeId, field.Bounds, label.Bounds,
                        RelativeTo(field.Bounds, window.Bounds), RelativeTo(label.Bounds, window.Bounds)));
            }
            if (result == null) return null;
        }
        token.ThrowIfCancellationRequested();
        return result;
    }

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
            if (!CoversVisibleChildren(graph, elements)) return null;
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

    static bool CoversVisibleChildren(WindowGraphSnapshot graph, List<ObservedElement> elements)
    {
        if (elements == null) return false;
        // A native child that is visible but absent from UIA cannot be covered by the observed shape.
        var visibleHandles = elements.Select(element => element.Hwnd).ToHashSet();
        return !graph.Windows.Any(child => child.Visible && child.IsChild && !visibleHandles.Contains(child.Hwnd));
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

        if (!MatchesNativeTopLevel(root, target.Pid, out bool enabled) || !enabled) return false;

        if (root.OwnerHwnd != 0 && !graph.Windows.Any(window => window.Hwnd == root.OwnerHwnd &&
            window.Hwnd != root.Hwnd && !window.IsChild && !window.Visible && NativePidMatches(new IntPtr(window.Hwnd), target.Pid)))
            return false;

        className = ReadClass(new IntPtr(root.Hwnd));
        return className != null;
    }

    static bool MatchesNativeTopLevel(WindowObservation window, int pid, out bool enabled)
    {
        var hwnd = new IntPtr(window.Hwnd);
        enabled = false;
        if (!NativePidMatches(hwnd, pid) || !N.IsWindowVisible(hwnd) || N.IsIconic(hwnd) ||
            N.GetAncestor(hwnd, N.GA_ROOT) != hwnd || N.GetDpiForWindow(hwnd) != (uint)window.Dpi ||
            GetWindow(hwnd, GwOwner).ToInt64() != window.OwnerHwnd ||
            N.DwmGetWindowAttribute(hwnd, N.DWMWA_CLOAKED, out int cloak, sizeof(int)) != 0 || cloak != 0 ||
            N.DwmGetWindowAttribute(hwnd, N.DWMWA_EXTENDED_FRAME_BOUNDS, out N.RECT frame, Marshal.SizeOf<N.RECT>()) != 0 ||
            !TryRectangle(frame, out var frameBounds) || frameBounds != window.Bounds)
            return false;
        enabled = IsWindowEnabled(hwnd);
        return true;
    }

    static bool MatchesScene(PasswordEvidenceScene scene, WindowObservation root, string rootClass) =>
        scene != null && scene.Fields is { Length: > 0 } && scene.Dpi == root.Dpi &&
        scene.WindowWidth == root.Bounds.Width && scene.WindowHeight == root.Bounds.Height &&
        MatchesClass(rootClass, scene.WindowClassPrefix);

    static List<ObservedElement> ReadVisibleStructure(EvidenceTarget target, WindowObservation window,
        EvidencePolicy policy, CancellationToken token, long excludedPeer = 0, StructureTrace trace = null)
    {
        int count = 0;
        int elementLimit = Math.Clamp(policy.MaxElements, 1, MaximumElements);
        int depthLimit = Math.Clamp(policy.MaxDepth, 1, MaximumDepth);
        var observed = new List<ObservedElement>();
        var walker = TreeWalker.RawViewWalker;

        bool Visit(AutomationElement element, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (trace != null) trace.DeepestDepth = Math.Max(trace.DeepestDepth, depth);
            if (depth > depthLimit) return trace?.Fail("uia-depth-limit") ?? false;
            if (++count > elementLimit) return trace?.Fail("uia-element-limit") ?? false;
            if (trace != null) trace.VisitedElements = count;

            var current = element.Current;
            // A separate observation covers the native top-level peer when UIA exposes it under this window.
            if (depth > 0 && excludedPeer != 0 && unchecked((uint)current.NativeWindowHandle) == excludedPeer)
                return current.ProcessId == target.Pid || (trace?.Fail("uia-process-mismatch") ?? false);
            bool password = current.IsPassword;
            if (current.ProcessId != target.Pid) return trace?.Fail("uia-process-mismatch") ?? false;
            bool offscreen = current.IsOffscreen;
            if (offscreen)
            {
                // Hidden password reports cannot establish that the credential stays inside the approved mask.
                if (depth == 0) return trace?.Fail("uia-root-offscreen") ?? false;
                if (password) return trace?.Fail("uia-hidden-password") ?? false;
            }
            else
            {
                if (!TryRectangle(current.BoundingRectangle, out var bounds)) return trace?.Fail("uia-bounds-invalid") ?? false;
                int type = current.ControlType?.Id ?? 0;
                long handle = unchecked((uint)current.NativeWindowHandle);
                if (depth == 0)
                {
                    if (handle != window.Hwnd) return trace?.Fail("uia-root-hwnd-mismatch") ?? false;
                    if (type != UiaWindowControl) return trace?.Fail("uia-root-control-type-mismatch") ?? false;
                    if (!bounds.Contains(window.Bounds)) return trace?.Fail("uia-root-bounds-mismatch") ?? false;
                    bounds = window.Bounds;
                }
                else if (!window.Bounds.Contains(bounds)) return trace?.Fail("uia-child-outside-window") ?? false;

                var relative = RelativeTo(bounds, window.Bounds);
                bool enabled = current.IsEnabled;
                if (depth == 0 && trace != null) trace.UiaRootEnabled = enabled;
                observed.Add(new(element, handle, bounds,
                    new(type, relative.X, relative.Y, relative.Width, relative.Height, password, enabled)));
            }

            for (var child = walker.GetFirstChild(element); child != null; child = walker.GetNextSibling(child))
                if (!Visit(child, depth + 1)) return false;
            return true;
        }

        AutomationElement root;
        try { root = AutomationElement.FromHandle(new IntPtr(window.Hwnd)); }
        catch (Exception error) when (trace != null && IsUiaObservationError(error))
        {
            trace.Fail("uia-root-unavailable");
            return null;
        }
        if (root != null) return Visit(root, 0) ? observed : null;
        trace?.Fail("uia-root-unavailable");
        return null;
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
