using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OperationRecorderApp.ExternalEvidence;

public sealed class EvidenceStep
{
    public Guid StepId { get; set; }
    public Guid Token { get; set; }
    public int Sequence { get; set; }
    public string OperationName { get; set; }
    public EvidenceState Expected { get; set; }
    public EvidenceState Actual { get; set; }
    public string Outcome { get; set; }
    public string Status { get; set; } = "before-unconfirmed";
    public string Before { get; set; }
    public string After { get; set; }
    public string Failure { get; set; }
}
public sealed class EvidenceSession : IDisposable
{
    readonly IEvidenceBackend backend;
    readonly IEvidenceTargetValidator validator;
    readonly Dictionary<Guid, EvidenceStep> steps = new();
    readonly Dictionary<Guid, string> requestHashes = new();
    readonly SemaphoreSlim gate = new(1, 1);
    FileStream sessionLock;
    Guid sessionId;
    EvidenceTarget target;
    EvidencePolicy policy;
    string directory;
    DateTimeOffset started;
    bool stopped;
    public EvidenceSession(IEvidenceBackend backend, IEvidenceTargetValidator validator) { this.backend = backend; this.validator = validator; }
    public async Task<EvidenceReply> HandleAsync(EvidenceRequest request, CancellationToken cancellationToken = default)
    {
        Guid id = request?.RequestId ?? Guid.Empty;
        EvidenceReply Reply(string status, EvidenceStep step = null, bool allowed = false, DateTimeOffset? saved = null, params string[] paths) => new()
        { RequestId = id, SessionId = sessionId, StepId = step?.StepId ?? request?.StepId ?? Guid.Empty, StepToken = step?.Token ?? Guid.Empty,
            Status = status, OperationAllowed = allowed, SavedAt = saved, EvidencePaths = paths,
            PendingSteps = steps.Values.Where(s => s.Status != "completed").Select(s => s.StepId).ToArray() };
        bool entered = false;
        EvidenceStep processing = null;
        try
        {
            if (request == null || request.SchemaVersion != 1 || id == Guid.Empty || request.SessionId == Guid.Empty ||
                request.DeadlineUtc <= DateTimeOffset.UtcNow || request.DeadlineUtc > DateTimeOffset.UtcNow.AddSeconds(30)) return Reply("invalid-request");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(request.DeadlineUtc - DateTimeOffset.UtcNow);
            await gate.WaitAsync(deadline.Token); entered = true;
            string hash = EvidenceFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(request, EvidenceProtocol.Json));
            if (requestHashes.TryGetValue(id, out var previous)) return Reply(previous == hash ? "duplicate-request" : "request-conflict");
            if (requestHashes.Count >= 20000) return Reply("session-limit");
            requestHashes.Add(id, hash);
            if (request.Method == "StartSession")
            {
                if (sessionId != Guid.Empty) return Reply(request.SessionId == sessionId ? "already-started" : "session-conflict");
                if (request.Target == null || !validator.Matches(request.Target)) return Reply("target-mismatch");
                var selectedPolicy = request.Policy ?? new();
                if (selectedPolicy.MaxElements < 1 || selectedPolicy.MaxElements > 256 || selectedPolicy.MaxDepth < 1 || selectedPolicy.MaxDepth > 16 ||
                    selectedPolicy.RecordVideo && (string.IsNullOrWhiteSpace(selectedPolicy.FfmpegPath) || !File.Exists(selectedPolicy.FfmpegPath))) return Reply("unsupported-policy");
                string output = Path.GetFullPath(request.OutputDirectory ?? throw new EvidenceUnavailableException("invalid-output"));
                EvidenceFiles.RequireLocalUnlinkedPath(output);
                if (Directory.Exists(output)) return Reply("output-exists");
                Directory.CreateDirectory(output);
                sessionLock = new FileStream(Path.Combine(output, "external.lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                directory = output; target = request.Target; policy = selectedPolicy; started = DateTimeOffset.UtcNow; sessionId = request.SessionId;
                EvidenceFiles.AtomicJson(Path.Combine(directory, "external-session.json"), new { schemaVersion = 1, sessionId, started, target, policy, status = "open" });
                SaveCompatibility();
                return Reply("started") with { Capabilities = new[] { "durable-before-after", "explicit-steps", "opaque-all", "geometry-only", "snapshot-video-segments", "no-input-hooks" } };
            }
            if (sessionId == Guid.Empty || request.SessionId != sessionId) return Reply("session-mismatch");
            if (request.Method is "Flush" or "StopSession")
            {
                SaveCompatibility();
                EvidenceFiles.AtomicJson(Path.Combine(directory, "checkpoint.json"), new { schemaVersion = 1, sessionId, stopped = request.Method == "StopSession" || stopped, steps = steps.Values.ToArray() });
                if (request.Method == "StopSession") stopped = true;
                return Reply(stopped ? "stopped" : "flushed", saved: DateTimeOffset.UtcNow, paths: new[] { Path.Combine(directory, "checkpoint.json"), Path.Combine(directory, "session.json"), Path.Combine(directory, "events.jsonl") });
            }
            if (stopped) return Reply("session-stopped");
            if (!validator.Matches(target)) return Reply("target-mismatch");
            if (!ValidState(request.ExpectedStateMetadata) || !ValidState(request.ActualStateMetadata)) return Reply("invalid-metadata");
            if (request.Method == "BeginStep")
            {
                if (request.StepId == Guid.Empty || !new[] { "invoke", "set-value", "native-message", "select", "observe", "custom" }.Contains(request.OperationName)) return Reply("invalid-request");
                if (steps.TryGetValue(request.StepId, out var duplicate)) return Reply("duplicate-step", duplicate, paths: ExistingPaths(duplicate));
                if (steps.Count >= 10000) return Reply("session-limit");
                if (steps.Values.Any(s => s.Status != "completed")) return Reply("pending-step");
                var step = new EvidenceStep { StepId = request.StepId, Token = Guid.NewGuid(), Sequence = steps.Count + 1, OperationName = request.OperationName, Expected = request.ExpectedStateMetadata };
                processing = step;
                steps.Add(step.StepId, step); SaveStep(step);
                var commit = await Capture(step, "before", request, deadline.Token);
                step.Before = Path.Combine(PhaseDirectory(step, "before"), "commit.json"); step.Status = "before-saved"; SaveStep(step);
                if (DateTimeOffset.UtcNow >= request.DeadlineUtc || cancellationToken.IsCancellationRequested) return Reply("save-unconfirmed", step, paths: new[] { step.Before });
                return Reply("before-saved", step, true, commit.SavedAt, step.Before);
            }
            EvidenceStep active = request.Method == "RecordFailure" ? steps.GetValueOrDefault(request.StepId) : steps.Values.FirstOrDefault(s => s.Token == request.StepToken && request.StepToken != Guid.Empty);
            if (active == null) return Reply("unknown-step");
            processing = active;
            if (request.Method == "EndStep")
            {
                if (active.Status == "completed") return Reply("duplicate-step", active, paths: ExistingPaths(active));
                if (active.Status != "before-saved") return Reply("step-indeterminate", active, paths: ExistingPaths(active));
                if (request.Outcome is not ("success" or "failure" or "unknown")) return Reply("invalid-request", active);
                active.Status = "after-unconfirmed"; active.Outcome = request.Outcome; active.Actual = request.ActualStateMetadata; SaveStep(active);
                var commit = await Capture(active, "after", request, deadline.Token);
                active.After = Path.Combine(PhaseDirectory(active, "after"), "commit.json"); active.Status = "completed"; SaveStep(active); SaveCompatibility();
                return Reply("after-saved", active, saved: commit.SavedAt, paths: new[] { active.After });
            }
            if (request.Method == "RecordFailure")
            {
                if (!new[] { "unexpected-state", "timeout", "capture-unavailable", "save-unavailable", "caller-exit", "operation-failed" }.Contains(request.ErrorCode)) return Reply("invalid-request", active);
                if (active.Failure != null || Directory.Exists(PhaseDirectory(active, "failure"))) return Reply("duplicate-step", active, paths: ExistingPaths(active));
                EvidenceFiles.AtomicJson(Path.Combine(StepDirectory(active), "failure-request.json"), new { request.ErrorCode, schemaVersion = 1 });
                var commit = await Capture(active, "failure", request, deadline.Token);
                active.Failure = Path.Combine(PhaseDirectory(active, "failure"), "commit.json"); SaveStep(active);
                return Reply("failure-saved", active, saved: commit.SavedAt, paths: new[] { active.Failure });
            }
            return Reply("invalid-method");
        }
        catch (OperationCanceledException) { return Reply(cancellationToken.IsCancellationRequested ? "caller-unavailable" : "deadline-exceeded", processing); }
        catch (EvidenceUnavailableException error) { return Reply(error.Code, processing); }
        catch { return Reply("save-unavailable", processing); } // Never expose paths supplied by UI providers, exception text, or stack traces.
        finally { if (entered) gate.Release(); }
    }
    static bool ValidState(EvidenceState state) => state == null ||
        ((state.ControlTypeId == null || state.ControlTypeId is >= 50000 and <= 50100) &&
        (state.FingerprintSha256 == null || EvidenceProtocol.IsHash(state.FingerprintSha256)));
    string StepDirectory(EvidenceStep step) => Path.Combine(directory, "steps", step.StepId.ToString("N"));
    string PhaseDirectory(EvidenceStep step, string phase) => Path.Combine(StepDirectory(step), phase);
    void SaveStep(EvidenceStep step) => EvidenceFiles.AtomicJson(Path.Combine(StepDirectory(step), "step.json"), step);
    static string[] ExistingPaths(EvidenceStep step) => new[] { step.Before, step.After, step.Failure }.Where(p => p != null).ToArray();
    async Task<EvidenceCommit> Capture(EvidenceStep step, string phase, EvidenceRequest request, CancellationToken token)
    {
        string path = PhaseDirectory(step, phase);
        Directory.CreateDirectory(path);
        EvidenceFiles.AtomicJson(Path.Combine(path, "started.json"), new { schemaVersion = 1, request.RequestId, step.StepId, step.Token, phase });
        var work = new CaptureWork(sessionId, step.StepId, step.Token, phase, target, policy, path, started, request.DeadlineUtc);
        await backend.CaptureAndSaveAsync(work, token).WaitAsync(token);
        token.ThrowIfCancellationRequested();
        if (!validator.Matches(target)) throw new EvidenceUnavailableException("target-mismatch");
        return EvidenceFiles.Verify(work);
    }
    void SaveCompatibility()
    {
        if (directory == null) return;
        var complete = steps.Values.Where(s => s.Status == "completed").OrderBy(s => s.Sequence).ToArray();
        EvidenceFiles.AtomicJson(Path.Combine(directory, "session.json"), new { tool = "operation-recorder", source = "external-evidence", schemaVersion = 1,
            sessionId, mode = "Window", caption = "外部操作記録", start = started, end = DateTimeOffset.UtcNow, eventCount = complete.Length,
            targetWindow = new { hwnd = target.WindowHandle, title = "対象アプリ（文字情報非取得）" } });
        var events = complete.Select(s => JsonSerializer.Serialize(new { seq = s.Sequence, kind = "capture", time = started, tMs = (long)(JsonSerializer.Deserialize<EvidenceCommit>(File.ReadAllBytes(s.Before), EvidenceProtocol.Json).CapturedAt - started).TotalMilliseconds,
            stepId = s.StepId, before = new { file = Path.GetRelativePath(directory, Path.Combine(Path.GetDirectoryName(s.Before), "frame.png")).Replace('\\','/') },
            after = new { file = Path.GetRelativePath(directory, Path.Combine(Path.GetDirectoryName(s.After), "frame.png")).Replace('\\','/') } }, EvidenceProtocol.Json));
        EvidenceFiles.AtomicBytes(Path.Combine(directory, "events.jsonl"), Encoding.UTF8.GetBytes(string.Join("\n", events) + (complete.Length == 0 ? "" : "\n")));
        EvidenceFiles.AtomicJson(Path.Combine(directory, "edits.json"), new { steps = complete.ToDictionary(s => s.Sequence.ToString(), s => new { headline = "外部操作 " + s.OperationName, body = s.Outcome }) });
    }
    public void Dispose() { sessionLock?.Dispose(); gate.Dispose(); }
}
