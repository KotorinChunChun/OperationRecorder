using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OperationRecorderApp.ExternalEvidence;

public sealed partial class GroupEvidenceSession
{
    WindowObservation[] transitionInitialHelpers = Array.Empty<WindowObservation>();
    readonly ConcurrentDictionary<Guid, TransitionAttempt> transitionAttempts = new();
    bool transitionFinished;
    delegate EvidenceReply TransitionReply(string status, EvidenceStep step = null, GroupQuality quality = null, string[] paths = null);

    void RegisterTransitionAttempt(EvidenceStep step) => transitionAttempts.TryAdd(step.Token, new TransitionAttempt(id, step.StepId));

    sealed class TransitionAttempt(Guid sessionId, Guid stepId)
    {
        int reserved;
        internal bool Abandoned => Volatile.Read(ref reserved) == 2;
        internal bool TryReserve(EvidenceRequest request) => request.SessionId == sessionId &&
            TargetAndOutcomeValid(request, stepId) && request.GroupPolicy == null &&
            ValidState(request.ExpectedStateMetadata) && ValidState(request.ActualStateMetadata) &&
            Interlocked.CompareExchange(ref reserved, 1, 0) == 0;
        internal void Abandon() => Volatile.Write(ref reserved, 2);
    }

    void SealAbandonedTransition(Guid token)
    {
        var pending = steps.Values.SingleOrDefault(step => step.Token == token);
        if (pending?.Status != "before-saved") return;
        pending.Status = "after-unconfirmed";
        pending.Outcome = "unknown";
        try { Save(pending); }
        catch (IOException) { } // The token remains consumed even if durable storage is unavailable.
        catch (UnauthorizedAccessException) { }
        catch (EvidenceUnavailableException) { }
    }

    static bool TargetAndOutcomeValid(EvidenceRequest request, Guid stepId) =>
        request.Target == null && request.WindowApprovals == null &&
        (request.StepId == Guid.Empty || request.StepId == stepId) && request.Outcome is "success" or "failure" or "unknown";

    async Task<EvidenceReply> EndTransitionAsync(EvidenceStep step, EvidenceRequest request, TransitionReply reply,
        bool attemptReserved, CancellationToken token)
    {
        if (step.Status == "completed") return reply("duplicate-step", step);
        if (step.Status != "before-saved") return reply("step-indeterminate", step);
        if (!TargetAndOutcomeValid(request, step.StepId)) return reply("invalid-request", step);
        if (!attemptReserved)
        {
            if (transitionAttempts.TryGetValue(step.Token, out var attempt) && attempt.Abandoned)
                SealAbandonedTransition(step.Token);
            return reply("step-indeterminate", step);
        }

        // Consume the operation token before native observation or capture. A lost ACK or
        // interrupted capture must never cause a caller to perform the operation again.
        step.Status = "after-unconfirmed";
        step.Outcome = request.Outcome == "failure" ? "failure" : "unknown";
        step.Actual = request.ActualStateMetadata;
        Save(step);
        if (request.Outcome != "success" || policy.TransitionAfterRoiProfile == null || registry == null)
            return reply("transition-unconfirmed", step);

        string path = Path.GetDirectoryName(CommitPath(step, "after"));
        string staging = path + ".pending-" + Guid.NewGuid().ToString("N");
        bool moved = false;
        bool confirmed = false;
        try
        {
            var before = ReadWork(Path.GetDirectoryName(CommitPath(step, "before")), id, step, "before", target, started, request.DeadlineUtc);
            var beforeManifest = VerifyTransitionBefore(before, policy, appHash);
            if (beforeManifest == null)
                return reply("transition-unconfirmed", step);

            long hwnd = ObserveTransitionCandidate();
            if (hwnd == 0) return reply("transition-unconfirmed", step);
            var destination = target with { WindowHandle = hwnd };
            using var afterRegistry = new WindowGraphRegistry();
            var observed = afterRegistry.Snapshot(destination, policy.MaxWindows);
            var afterPolicy = WindowTransition.DerivePolicy(target, before, destination, observed, transitionInitialHelpers, registry.IsStillTracked);
            var plan = new GroupCapturePlan(observed, afterPolicy, appHash, destination);
            var work = new CaptureWork(id, step.StepId, step.Token, "after", destination, new(), staging, started, request.DeadlineUtc, GroupPlan: plan);

            EvidenceFiles.RequireLocalUnlinkedPath(staging);
            EvidenceFiles.RequireLocalUnlinkedPath(path);
            if (Directory.Exists(path) || Directory.Exists(staging)) throw new EvidenceUnavailableException("output-exists");
            var manifest = await CaptureCore(work, request.RequestId, token, waitForWorkerCleanup: true, observe: afterRegistry.Snapshot);
            if (!TransitionQuality(manifest) || beforeManifest.CollectionEnded > manifest.CollectionStarted)
                return reply("transition-unconfirmed", step, manifest.Quality);

            bool ConfirmLive()
            {
                if (ObserveTransitionCandidate() != destination.WindowHandle ||
                    EvidenceFiles.HashFile(target.ExePath) != appHash)
                    return false;
                var live = afterRegistry.Snapshot(destination, afterPolicy.MaxWindows);
                if (!PasswordGroupEvidenceWorker.SameGroup(observed, live)) return false;
                var livePolicy = WindowTransition.DerivePolicy(target, before, destination, live, transitionInitialHelpers, registry.IsStillTracked);
                long now = WgcCaptureBackend.Clock100ns();
                return WindowTransition.SamePolicy(afterPolicy, livePolicy) &&
                    manifest.Windows.Where(window => window.CaptureStatus == "fresh").All(window =>
                        window.FrameTime100ns is long time && time <= now && now - time <= afterPolicy.MaxFrameAgeMs * 10000L);
            }

            token.ThrowIfCancellationRequested();
            if (!ConfirmLive()) throw new EvidenceUnavailableException("transition-unconfirmed");
            Directory.Move(staging, path);
            moved = true;
            if (!ConfirmLive()) throw new EvidenceUnavailableException("transition-unconfirmed");
            token.ThrowIfCancellationRequested();
            // Historical inspection treats this single durable update as the completion boundary.
            step.After = CommitPath(step, "after");
            step.Status = "completed";
            step.Outcome = "success";
            Save(step);
            confirmed = transitionFinished = true;
            return reply("after-saved", step, manifest.Quality, new[] { step.After }) with
            {
                WindowGraph = observed, SavedAt = DateTimeOffset.UtcNow,
                CollectionEndedUtc = manifest.CollectionEnded.ToUniversalTime()
            };
        }
        catch (EvidenceUnavailableException e) when (e.Code is "target-mismatch" or "window-observation-unavailable" or
            "window-changed" or "window-generation-mismatch" or "capture-unavailable")
        {
            throw new EvidenceUnavailableException("transition-unconfirmed");
        }
        finally
        {
            if (moved && !confirmed)
            {
                step.Status = "after-unconfirmed";
                step.After = null;
                step.Outcome = "unknown";
                try { Save(step); }
                finally { RemoveTransitionDirectory(path); }
            }
            RemoveTransitionDirectory(staging);
        }
    }

    long ObserveTransitionCandidate()
    {
        var windows = WindowGraphRegistry.ObserveTransitionTopWindows(target, policy.MaxWindows);
        return WindowTransition.Select(target, windows);
    }

    EvidenceReply RecordTransitionFailure(EvidenceStep step, EvidenceRequest request, TransitionReply reply)
    {
        if (step.Status == "completed") return reply("duplicate-step", step);
        if (request.Target != null || !new[] {"unexpected-state","timeout","capture-unavailable","save-unavailable","caller-exit","operation-failed"}.Contains(request.ErrorCode))
            return reply("invalid-request", step);
        string path = Path.Combine(directory, "steps", step.StepId.ToString("N"), "failure-request.json");
        if (File.Exists(path)) return reply("duplicate-step", step);
        step.Status = "after-unconfirmed";
        step.Outcome ??= "unknown";
        Save(step);
        EvidenceFiles.AtomicJson(path, new { schemaVersion = 2, request.ErrorCode });
        return reply("failure-recorded", step);
    }

    static void RemoveTransitionDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        EvidenceFiles.RequireLocalUnlinkedPath(path);
        Directory.Delete(path, true);
    }

    static CaptureWork ReadWork(string path, Guid sessionId, EvidenceStep step, string phase, EvidenceTarget originalTarget,
        DateTimeOffset start, DateTimeOffset deadline)
    {
        EvidenceFiles.RequireLocalUnlinkedPath(path);
        var plan = JsonSerializer.Deserialize<GroupCapturePlan>(ReadMetadata(Path.Combine(path, "plan.json")), EvidenceProtocol.Json);
        return new CaptureWork(sessionId, step.StepId, step.Token, phase, plan.TransitionAfterTarget ?? originalTarget,
            new(), path, start, deadline, GroupPlan: plan);
    }

    static GroupEvidenceManifest ReadVerifiedManifest(CaptureWork work)
    {
        EvidenceFiles.Verify(work);
        var manifest = JsonSerializer.Deserialize<GroupEvidenceManifest>(ReadMetadata(Path.Combine(work.Directory, "group.json")), EvidenceProtocol.Json);
        GroupEvidenceValidation.Verify(work, manifest);
        return manifest;
    }

    static bool TransitionQuality(GroupEvidenceManifest manifest) =>
        manifest.Quality.RequirementsMet && manifest.Quality.DiagnosticStatus == "sufficient";

    static GroupEvidenceManifest VerifyTransitionBefore(CaptureWork before, GroupEvidencePolicy sessionPolicy, string hash)
    {
        if (before.GroupPlan.TransitionAfterTarget != null || before.GroupPlan.AppSha256 != hash ||
            sessionPolicy?.PasswordProfile == null || sessionPolicy.TransitionAfterRoiProfile == null ||
            !WindowTransition.SamePolicy(sessionPolicy with { Approvals = before.GroupPlan.Policy.Approvals }, before.GroupPlan.Policy))
            return null;
        var manifest = ReadVerifiedManifest(before);
        return TransitionQuality(manifest) ? manifest : null;
    }

    static bool VerifyTransitionHistory(CaptureWork before, CaptureWork after, GroupEvidenceManifest afterManifest,
        EvidenceTarget source, GroupEvidencePolicy sessionPolicy, string hash, WindowObservation[] initialHelpers)
    {
        var beforeManifest = VerifyTransitionBefore(before, sessionPolicy, hash);
        if (beforeManifest == null || !TransitionQuality(afterManifest) ||
            !WindowTransition.MatchesSource(source, after.GroupPlan.TransitionAfterTarget))
            return false;
        var expected = WindowTransition.DerivePolicy(source, before, after.Target, after.GroupPlan.Snapshot, initialHelpers, _ => true);
        return WindowTransition.SamePolicy(after.GroupPlan.Policy, expected) &&
            beforeManifest.CollectionEnded <= afterManifest.CollectionStarted;
    }
}
