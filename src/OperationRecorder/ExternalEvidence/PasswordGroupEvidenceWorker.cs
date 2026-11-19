using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OperationRecorderApp.ExternalEvidence;

internal static class PasswordGroupEvidenceWorker
{
    internal static bool IsKnownGroup(WindowGraphSnapshot graph, EvidenceTarget target, IReadOnlyList<WindowApproval> approvals)
    {
        if (graph.Truncated || !approvals.Any(a => a.Required && a.Hwnd == target.WindowHandle)) return false;
        var root = graph.Windows.SingleOrDefault(window => window.Hwnd == target.WindowHandle);
        if (root == null || root.Relation != "root" || root.Pid != target.Pid || root.IsChild || !root.Visible || root.Minimized || root.Cloaked)
            return false;

        return graph.Windows.All(window => window.Pid == target.Pid && window.Relation != "foreign" &&
            (window.Hwnd == root.Hwnd || window.IsChild || !window.Visible) &&
            approvals.Any(approval => approval.Hwnd == window.Hwnd && approval.WindowId == window.WindowId &&
                approval.Generation == window.Generation && (!approval.Required || window.Hwnd == root.Hwnd)));
    }

    internal static bool SameGroup(WindowGraphSnapshot first, WindowGraphSnapshot next) => !next.Truncated &&
        first.Revision == next.Revision && first.Windows.OrderBy(w => w.Hwnd).SequenceEqual(next.Windows.OrderBy(w => w.Hwnd));

    internal static async Task CaptureAsync(CaptureWork work, CancellationToken token)
    {
        var plan = work.GroupPlan;
        var profile = PasswordEvidenceCatalog.Load(plan.Policy.PasswordProfile);
        if (!plan.Policy.RequirePublicDiagnostic || plan.Policy.Disclosure != null || plan.Policy.RoiProfile != null ||
            !PasswordEvidenceCatalog.MatchesApplication(profile, work.Target.ExePath, plan.AppSha256) ||
            EvidenceFiles.HashFile(work.Target.ExePath) != plan.AppSha256)
            throw new EvidenceUnavailableException("target-mismatch");

        var started = DateTimeOffset.UtcNow;
        using var registry = new WindowGraphRegistry();
        var first = registry.Snapshot(work.Target, plan.Policy.MaxWindows);
        var approvals = plan.Policy.Approvals ?? Array.Empty<WindowApproval>();
        var root = plan.Snapshot.Windows.Single(window => window.Hwnd == work.Target.WindowHandle);
        bool coherent = GroupEvidenceContract.Fingerprint(first) == GroupEvidenceContract.Fingerprint(plan.Snapshot) && !first.Truncated;
        string reason = "password-layout-unproven";
        byte[] image = null;
        EvidenceUi ui = null;
        PasswordMaskEvidence mask = null;
        long? frameTime = null;

        if (coherent && IsKnownGroup(plan.Snapshot, work.Target, approvals))
        {
            var nativeRoot = first.Windows.Single(window => window.Hwnd == root.Hwnd);
            var before = PasswordWindowObserver.Match(work.Target, nativeRoot, first, profile, work.Phase, work.Policy, token);
            if (before != null)
            {
                try
                {
                    var limit = DateTimeOffset.UtcNow.AddSeconds(3);
                    using var frame = await new WgcCaptureBackend().CaptureAsync(root.Hwnd,
                        work.DeadlineUtc < limit ? work.DeadlineUtc : limit, token);
                    long age = await GroupEvidenceWorker.MeasureConfirmedFrameAgeAsync(frame.FrameTime100ns, token);
                    if (age < 0 || age > plan.Policy.MaxFrameAgeMs * TimeSpan.TicksPerMillisecond)
                    {
                        reason = "password-frame-unproven";
                    }
                    else if (frame.ContentSize == root.Bounds.Size && (long)frame.Bitmap.Width * frame.Bitmap.Height <= plan.Policy.MaxTotalPixels)
                    {
                        var next = registry.Snapshot(work.Target, plan.Policy.MaxWindows);
                        var after = PasswordWindowObserver.Match(work.Target, nativeRoot, next, profile, work.Phase, work.Policy, token);
                        var last = registry.Snapshot(work.Target, plan.Policy.MaxWindows);
                        coherent = SameGroup(first, next) && SameGroup(first, last) && EvidenceFiles.HashFile(work.Target.ExePath) == plan.AppSha256;
                        if (coherent && after != null && before.Scene.SceneId == after.Scene.SceneId && before.Fields.SequenceEqual(after.Fields) &&
                            PasswordWindowObserver.VerifyNative(after.Scene, work.Target, nativeRoot, last, after.Fields))
                        {
                            var candidate = PasswordEvidenceGate.Mask(frame.Bitmap, after.Scene);
                            age = WgcCaptureBackend.Clock100ns() - frame.FrameTime100ns;
                            if (age >= 0 && age <= plan.Policy.MaxFrameAgeMs * TimeSpan.TicksPerMillisecond)
                            {
                                image = candidate;
                                ui = after.Ui;
                                frameTime = frame.FrameTime100ns;
                                mask = new(after.Scene.SceneId, after.Fields);
                                reason = "password-masked";
                            }
                            else reason = "password-frame-unproven";
                        }
                    }
                }
                catch (EvidenceUnavailableException)
                {
                    reason = "password-frame-unproven";
                }
            }
        }

        token.ThrowIfCancellationRequested();
        var rows = new List<WindowEvidence>();
        byte[] uiBytes = image == null ? null : JsonSerializer.SerializeToUtf8Bytes(ui, EvidenceProtocol.Json);
        foreach (var window in plan.Snapshot.Windows)
        {
            var approval = approvals.FirstOrDefault(grant => grant.Hwnd == window.Hwnd && grant.WindowId == window.WindowId && grant.Generation == window.Generation);
            if (approval == null || window.Relation == "foreign" || approval.Role == "contained")
            {
                bool contained = approval?.Role == "contained" && window.Relation != "foreign" &&
                    GroupEvidenceContract.CoveredChild(window, plan.Snapshot, approvals);
                rows.Add(new(window, contained ? "contained" : "unknown", "structure-only", null, null, 0, 0,
                    null, null, null, null, "not-captured", "opaque-only", false,
                    contained ? "included-in-parent" : "unapproved-window"));
            }
            else if (window.Hwnd == root.Hwnd && image != null)
            {
                string prefix = "windows/" + window.WindowId.ToString("N") + "/";
                string imageFile = prefix + "frame.png", uiFile = prefix + "ui.json";
                rows.Add(new(window, approval.Role, "wgc-window", null, frameTime, root.Bounds.Width, root.Bounds.Height,
                    imageFile, EvidenceFiles.Hash(image), uiFile, EvidenceFiles.Hash(uiBytes),
                    "fresh", "password-masked", true, reason, PasswordMask: mask));
            }
            else
            {
                rows.Add(new(window, approval.Role, "wgc-window", null, null, 0, 0, null, null, null, null,
                    "capture-unavailable", "blocked", false, window.Hwnd == root.Hwnd ? reason : "capture-unavailable"));
            }
        }

        var quality = GroupEvidenceValidation.Evaluate(plan, rows, coherent);
        // A contradictory mask must not leave candidate pixels available as a successful artifact.
        if (image != null && !quality.RequirementsMet) throw new EvidenceUnavailableException("save-unavailable");
        if (image != null)
        {
            token.ThrowIfCancellationRequested();
            var row = rows.Single(window => window.PasswordMask != null);
            EvidenceFiles.AtomicBytes(Path.Combine(work.Directory, row.ImageFile), image);
            EvidenceFiles.AtomicBytes(Path.Combine(work.Directory, row.UiFile), uiBytes);
        }
        var manifest = new GroupEvidenceManifest(2, work.SessionId, work.StepId, work.StepToken, work.Phase, plan.Snapshot.Revision,
            EvidenceFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(plan.Policy, EvidenceProtocol.Json)),
            started, DateTimeOffset.UtcNow, rows.ToArray(), quality);
        EvidenceFiles.AtomicJson(Path.Combine(work.Directory, "group.json"), manifest);
        EvidenceFiles.Save(work, image ?? EvidenceFiles.OpaquePng(new Size(16, 16)), new EvidenceUi(Array.Empty<EvidenceNode>(), false), DateTimeOffset.UtcNow);
    }
}
