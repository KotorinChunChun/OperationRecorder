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
    PasswordLayoutWindow Login = null, PasswordLayoutWindow Background = null);

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

    internal static T ObserveStable<T>(WindowGraphSnapshot graph, Func<WindowGraphSnapshot> snapshot,
        Func<WindowGraphSnapshot, T> observe) where T : class
    {
        var first = observe(graph);
        var next = snapshot();
        return PasswordGroupEvidenceWorker.SameGroup(graph, next) &&
            EqualityComparer<T>.Default.Equals(first, observe(next)) &&
            PasswordGroupEvidenceWorker.SameGroup(graph, snapshot()) ? first : null;
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
