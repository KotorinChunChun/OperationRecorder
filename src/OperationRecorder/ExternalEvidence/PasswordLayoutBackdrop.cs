using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OperationRecorderApp.ExternalEvidence;

internal sealed record PasswordLayoutWindow(long Hwnd, long OwnerHwnd, string WindowClass, bool Visible, bool Enabled, int Dpi,
    [property: JsonConverter(typeof(ApprovedRoiRectangleConverter))] Rectangle CaptureBounds, string UiSha256 = null);

internal sealed record PasswordLayoutDiagnostics(string Reason, string Ownership = null, string LayoutSha256 = null,
    PasswordLayoutWindow Login = null, PasswordLayoutWindow Background = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PasswordLayoutStructureDiagnostic LoginStructure = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PasswordLayoutStructureDiagnostic BackgroundStructure = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string FailureRole = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PasswordLayoutChange Change = null);

internal sealed record PasswordLayoutChange(string Stage, string[] Kinds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[] GraphRevisionSources = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WindowGraphEventInterval WinEvents = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[] SnapshotTruncationReasons = null);

/// <summary>Checks the window set for the read-only layout candidate tool.</summary>
internal static class PasswordLayoutBackdrop
{
    internal static (WindowObservation Login, WindowObservation Background, string Reason) Select(
        EvidenceTarget target, WindowGraphSnapshot graph)
    {
        if (graph?.Windows == null || graph.Truncated) return (null, null, "window-layout-unavailable");
        if (graph.Windows.Any(window => window.Pid != target.Pid || window.Relation == "foreign"))
            return (null, null, "foreign-window");

        var login = graph.Windows.SingleOrDefault(window => window.Hwnd == target.WindowHandle);
        if (login?.Relation != "root" || !ValidTopLevel(login)) return (null, null, "window-layout-unavailable");
        var additional = graph.Windows.Where(window => window.Hwnd != login.Hwnd && !window.IsChild && window.Visible)
            .Take(2).ToArray();
        if (additional.Length == 0) return (login, null, "background-window-missing");
        if (additional.Length != 1) return (login, null, "ambiguous-window-layout");
        if (!ValidTopLevel(additional[0])) return (login, null, "window-layout-unavailable");
        return (login, additional[0], null);
    }

    static bool ValidTopLevel(WindowObservation window) => window.Hwnd > 0 && window.WindowId != Guid.Empty &&
        window.Generation > 0 && !window.IsChild && window.ParentHwnd == 0 && window.Visible &&
        !window.Minimized && !window.Cloaked && window.Dpi > 0 && window.Bounds.Width > 0 && window.Bounds.Height > 0;

    internal static string GetOwnership(long login, long loginOwner, long background, long backgroundOwner)
    {
        if (loginOwner == background && backgroundOwner == 0) return "login-owned-by-background";
        if (backgroundOwner == login && loginOwner == 0) return "background-owned-by-login";
        return loginOwner == 0 && backgroundOwner == 0 ? "unowned" : null;
    }

    internal static (T Observation, PasswordLayoutChange Change) ObserveStable<T>(WindowGraphSnapshot graph,
        Func<WindowGraphSnapshot> snapshot, Func<WindowGraphSnapshot, T> observe,
        Func<T, T, (string Stage, string[] Kinds)?> compare) where T : class
    {
        var first = observe(graph);
        var next = snapshot();
        if (!PasswordGroupEvidenceWorker.SameGroup(graph, next))
            return (null, DescribeGraphChange("confirmation-snapshot", graph, next));
        if (compare(first, observe(next)) is { } difference)
            return (null, new(difference.Stage, difference.Kinds));
        var last = snapshot();
        return PasswordGroupEvidenceWorker.SameGroup(graph, last)
            ? (first, null) : (null, DescribeGraphChange("final-snapshot", graph, last));
    }

    static PasswordLayoutChange DescribeGraphChange(string stage, WindowGraphSnapshot first, WindowGraphSnapshot next)
    {
        var sources = first.Revision != next.Revision ? RevisionSources(first.RevisionSource, next.RevisionSource) : null;
        var events = sources?.Contains("win-event") == true
            ? WindowGraphEventInterval.Between(first.RevisionSource, next.RevisionSource) : null;
        return new(stage, GraphDifferences(first, next), sources, events, TruncationReasons(next));
    }

    internal static string[] TruncationReasons(WindowGraphSnapshot snapshot)
    {
        if (!snapshot.Truncated) return null;
        var flags = snapshot.RevisionSource?.Truncation ?? WindowGraphTruncation.None;
        var reasons = new List<string>(3);
        if (flags.HasFlag(WindowGraphTruncation.WindowLimit)) reasons.Add("window-limit");
        if (flags.HasFlag(WindowGraphTruncation.WindowReadFailed)) reasons.Add("window-read-failed");
        if (flags.HasFlag(WindowGraphTruncation.ConcurrentWinEvent)) reasons.Add("concurrent-win-event");
        return reasons.Count == 0 ? new[] { "unknown" } : reasons.ToArray();
    }

    static string[] RevisionSources(WindowGraphRevisionSource first, WindowGraphRevisionSource next)
    {
        if (first == null || next == null || !ReferenceEquals(first.Epoch, next.Epoch)) return new[] { "unknown" };
        var sources = new List<string>(2);
        if (first.TrackerRevision != next.TrackerRevision) sources.Add("tracker");
        if (first.WinEventCount != next.WinEventCount) sources.Add("win-event");
        return sources.Count > 0 ? sources.ToArray() : new[] { "unknown" };
    }

    static string[] GraphDifferences(WindowGraphSnapshot first, WindowGraphSnapshot next)
    {
        var changes = new HashSet<string>(StringComparer.Ordinal);
        if (next.Truncated) changes.Add("snapshot-truncated");
        if (first.Revision != next.Revision) changes.Add("graph-revision");

        var previousWindows = first.Windows.ToDictionary(window => window.Hwnd);
        if (!previousWindows.Keys.ToHashSet().SetEquals(next.Windows.Select(window => window.Hwnd)))
            changes.Add("window-set");
        foreach (var window in next.Windows)
        {
            if (!previousWindows.TryGetValue(window.Hwnd, out var previous)) continue;
            if (previous.WindowId != window.WindowId || previous.Generation != window.Generation || previous.Pid != window.Pid)
                changes.Add("window-identity");
            if (previous.ParentHwnd != window.ParentHwnd || previous.OwnerHwnd != window.OwnerHwnd ||
                previous.IsChild != window.IsChild || previous.Relation != window.Relation) changes.Add("window-relationship");
            if (previous.Visible != window.Visible || previous.Minimized != window.Minimized || previous.Cloaked != window.Cloaked)
                changes.Add("window-visibility");
            if (previous.Bounds != window.Bounds) changes.Add("window-geometry");
            if (previous.Dpi != window.Dpi) changes.Add("window-dpi");
        }
        return changes.OrderBy(change => change, StringComparer.Ordinal).ToArray();
    }

    internal static (string Stage, string[] Kinds)? CompareObservations(PasswordWindowObserver.BackdropObservation first,
        PasswordWindowObserver.BackdropObservation next)
    {
        var previous = first?.Diagnostics;
        var current = next?.Diagnostics;
        if ((previous?.Change ?? current?.Change) is { } nativeChange)
            return (nativeChange.Stage, nativeChange.Kinds);
        if (previous == null || current == null)
            return ("confirmation-observation", new[] { "observation-result" });

        // Keep the layout, binding and state checks that existed before diagnostic counters were added.
        // A rejected layout needs a stable failure code before it can be reported as the cause.
        bool sameFailure = previous.LayoutSha256 != null || (previous.FailureRole == current.FailureRole &&
            SameStructureFailure(previous.LoginStructure, current.LoginStructure) &&
            SameStructureFailure(previous.BackgroundStructure, current.BackgroundStructure));
        if (previous.Reason == current.Reason && previous.Ownership == current.Ownership &&
            previous.LayoutSha256 == current.LayoutSha256 && previous.Login == current.Login &&
            previous.Background == current.Background && first.Layout == next.Layout && sameFailure)
            return null;

        var changes = ObservationDifferences(previous, current).ToList();
        if (first.Layout != next.Layout) changes.Add("field-binding");
        if (changes.Count == 0) changes.Add("observation-result");
        return ("confirmation-observation", changes.OrderBy(change => change, StringComparer.Ordinal).ToArray());
    }

    static bool SameStructureFailure(PasswordLayoutStructureDiagnostic first, PasswordLayoutStructureDiagnostic next) =>
        first?.Code == next?.Code && first?.UiaRootEnabled == next?.UiaRootEnabled &&
        SameFailureElement(first?.FailureElement, next?.FailureElement);

    static bool SameFailureElement(PasswordLayoutFailureElement first, PasswordLayoutFailureElement next) =>
        first == next || first != null && next != null &&
        first.Depth == next.Depth && first.RawViewPath.SequenceEqual(next.RawViewPath) &&
        first.ControlTypeId == next.ControlTypeId && first.IsOffscreen == next.IsOffscreen &&
        first.IsPassword == next.IsPassword && first.BoundsCode == next.BoundsCode &&
        first.NativeWindowHandle == next.NativeWindowHandle && first.NativeBoundsCode == next.NativeBoundsCode;

    internal static string[] ObservationDifferences(PasswordLayoutDiagnostics first, PasswordLayoutDiagnostics next)
    {
        if (first == null || next == null) return new[] { "observation-result" };
        var changes = new HashSet<string>(StringComparer.Ordinal);
        if (first.Reason != next.Reason || first.FailureRole != next.FailureRole) changes.Add("observation-result");
        if (first.Ownership != next.Ownership) changes.Add("window-relationship");

        void AddWindowDifferences(PasswordLayoutWindow previous, PasswordLayoutWindow current,
            PasswordLayoutStructureDiagnostic previousStructure, PasswordLayoutStructureDiagnostic currentStructure)
        {
            if (previous == null || current == null)
            {
                if (previous != current) changes.Add("window-set");
            }
            else
            {
                if (previous.Hwnd != current.Hwnd || previous.WindowClass != current.WindowClass) changes.Add("window-identity");
                if (previous.OwnerHwnd != current.OwnerHwnd) changes.Add("window-relationship");
                if (previous.Visible != current.Visible) changes.Add("window-visibility");
                if (previous.Enabled != current.Enabled) changes.Add("native-root-enabled");
                if (previous.CaptureBounds != current.CaptureBounds) changes.Add("window-geometry");
                if (previous.Dpi != current.Dpi) changes.Add("window-dpi");
                if (previous.UiSha256 != current.UiSha256) changes.Add("uia-structure");
            }
            if (previousStructure?.UiaRootEnabled != currentStructure?.UiaRootEnabled) changes.Add("uia-root-enabled");
            if (previousStructure?.Code != currentStructure?.Code || previousStructure?.VisitedElements != currentStructure?.VisitedElements ||
                previousStructure?.DeepestDepth != currentStructure?.DeepestDepth) changes.Add("uia-traversal");
            if (!SameFailureElement(previousStructure?.FailureElement, currentStructure?.FailureElement))
                changes.Add("uia-failure-element");
        }

        AddWindowDifferences(first.Login, next.Login, first.LoginStructure, next.LoginStructure);
        AddWindowDifferences(first.Background, next.Background, first.BackgroundStructure, next.BackgroundStructure);
        return changes.OrderBy(change => change, StringComparer.Ordinal).ToArray();
    }

    internal static string LayoutHash(string appSha256, string appFileVersion, string ownership,
        PasswordLayoutWindow login, PasswordLayoutWindow background)
    {
        // Window handles are runtime identities. The review matches stable layout and relative physical geometry.
        var proof = new
        {
            schemaVersion = 1, purpose = "password-layout-backdrop", appSha256, appFileVersion, ownership,
            login = Fingerprint(login), background = Fingerprint(background),
            backgroundX = (long)background.CaptureBounds.X - login.CaptureBounds.X,
            backgroundY = (long)background.CaptureBounds.Y - login.CaptureBounds.Y
        };
        return EvidenceFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(proof, EvidenceProtocol.Json));
    }

    static object Fingerprint(PasswordLayoutWindow window) => new
    {
        window.WindowClass, window.Enabled, window.Visible, window.Dpi, window.UiSha256,
        window.CaptureBounds.Width, window.CaptureBounds.Height
    };
}
