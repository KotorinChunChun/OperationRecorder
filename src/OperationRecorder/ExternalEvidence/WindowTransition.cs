using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace OperationRecorderApp.ExternalEvidence;

internal static class WindowTransition
{
    internal static long Select(EvidenceTarget source, IReadOnlyList<WindowGraphInput> windows)
    {
        if (windows.Any(window => window.Pid != source.Pid || window.IsChild ||
            window.Hwnd == source.WindowHandle && window.Visible))
            return 0;

        var visible = windows.Where(window => window.Visible).ToArray();
        var roots = visible.Where(window => window.OwnerHwnd == 0).Take(2).ToArray();
        if (roots.Length != 1 || visible.Length > 2) return 0;
        long root = roots[0].Hwnd;
        return visible.All(window => (window.Hwnd == root || window.OwnerHwnd == root) &&
            !window.Cloaked && !window.Minimized && window.Bounds.Width > 0 && window.Bounds.Height > 0) ? root : 0;
    }

    internal static bool MatchesSource(EvidenceTarget source, EvidenceTarget destination) =>
        destination != null && destination.WindowHandle != 0 && destination.WindowHandle != source.WindowHandle &&
        destination with { WindowHandle = source.WindowHandle } == source;

    internal static bool SamePolicy(GroupEvidencePolicy expected, GroupEvidencePolicy actual) =>
        JsonSerializer.SerializeToUtf8Bytes(expected, EvidenceProtocol.Json).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(actual, EvidenceProtocol.Json));

    static bool SameStructure(WindowObservation left, WindowObservation right) =>
        left.Hwnd == right.Hwnd && left.Pid == right.Pid && left.IsChild == right.IsChild &&
        left.ParentHwnd == right.ParentHwnd && left.OwnerHwnd == right.OwnerHwnd;

    internal static GroupEvidencePolicy DerivePolicy(EvidenceTarget source, CaptureWork before, EvidenceTarget destination,
        WindowGraphSnapshot after, IReadOnlyList<WindowObservation> initialHelpers, Func<WindowObservation, bool> stillTracked)
    {
        if (!MatchesSource(source, destination) || after.Truncated ||
            after.Windows.Any(window => window.Pid != source.Pid || window.Relation == "foreign"))
            throw new EvidenceUnavailableException("transition-unconfirmed");
        var root = after.Windows.SingleOrDefault(window => window.Hwnd == destination.WindowHandle);
        if (root == null || root.Relation != "root" ||
            Select(source, after.Windows.Where(window => !window.IsChild).Select(window =>
                new WindowGraphInput(window.Hwnd, window.ParentHwnd, window.OwnerHwnd, window.Pid, window.IsChild,
                    window.Visible, window.Minimized, window.Cloaked, window.Bounds, window.Dpi, window.Relation)).ToArray()) != root.Hwnd)
            throw new EvidenceUnavailableException("transition-unconfirmed");

        var oldPlan = before.GroupPlan;
        var owned = after.Windows.SingleOrDefault(window => window.Hwnd != root.Hwnd && !window.IsChild && window.Visible);
        if (owned != null && (owned.Relation != "owned" ||
            !ApprovedRoiCatalog.Load(oldPlan.Policy.TransitionAfterRoiProfile).Scenes.Any(scene =>
                scene.Role == "owned" && scene.Phases.Contains("after"))))
            throw new EvidenceUnavailableException("transition-unconfirmed");
        var oldWindows = oldPlan.Snapshot.Windows.ToDictionary(window => window.Hwnd);
        var afterWindows = after.Windows.ToDictionary(window => window.Hwnd);
        var oldGrants = oldPlan.Policy.Approvals.ToDictionary(grant => grant.Hwnd);
        var helpers = initialHelpers.ToDictionary(window => window.Hwnd);
        WindowApproval Grant(WindowObservation observed, string role, bool required) =>
            new(observed.Hwnd, role, required, observed.WindowId, observed.Generation, AllowParentCrop: false);
        bool TryFindTop(WindowObservation window, out WindowObservation top)
        {
            var seen = new HashSet<long>();
            while (window.IsChild && seen.Add(window.Hwnd))
            {
                if (!afterWindows.TryGetValue(window.ParentHwnd, out window))
                {
                    top = null;
                    return false;
                }
            }
            top = window;
            return !window.IsChild;
        }
        bool IsKnownHidden(WindowObservation window)
        {
            if (window.Visible || !oldWindows.TryGetValue(window.Hwnd, out var previous) ||
                !oldGrants.TryGetValue(window.Hwnd, out var grant) ||
                grant.WindowId != previous.WindowId || grant.Generation != previous.Generation ||
                !SameStructure(window, previous) || !stillTracked(previous))
                return false;
            if (window.Hwnd == source.WindowHandle) return grant.Required;
            if (window.IsChild) return !grant.Required;
            return !grant.Required && !previous.Visible && helpers.TryGetValue(window.Hwnd, out var initial) &&
                initial.WindowId == previous.WindowId && initial.Generation == previous.Generation;
        }

        var grants = new List<WindowApproval>();
        foreach (var window in after.Windows)
        {
            if (window == root)
                grants.Add(Grant(window, "root", true));
            else if (window == owned)
                grants.Add(Grant(window, "owned", true));
            else if (window.IsChild && window.Relation == "child" && TryFindTop(window, out var top) && (top == root || top == owned))
                grants.Add(Grant(window, "contained", false));
            // The existing ROI worker never captures a window that is hidden. Preserve only hidden
            // source-group members whose generations were proven by the original registry.
            else if (IsKnownHidden(window) && TryFindTop(window, out var ancestor) && IsKnownHidden(ancestor))
                grants.Add(Grant(window, "transition-hidden", false));
            else
                throw new EvidenceUnavailableException("transition-unconfirmed");
        }
        return oldPlan.Policy with
        {
            Approvals = grants.ToArray(),
            RoiProfile = oldPlan.Policy.TransitionAfterRoiProfile,
            PasswordProfile = null,
            TransitionAfterRoiProfile = null
        };
    }
}
