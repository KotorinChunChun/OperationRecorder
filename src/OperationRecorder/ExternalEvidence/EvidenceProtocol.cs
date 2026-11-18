using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OperationRecorderApp.ExternalEvidence;

public static class EvidenceProtocol
{
    public const int SchemaVersion = 1, MaxMessageBytes = 65536;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };
    public static bool IsHash(string value) => value != null && System.Text.RegularExpressions.Regex.IsMatch(value, "\\A[0-9a-fA-F]{64}\\z");
}

public sealed record EvidenceTarget(int Pid, DateTimeOffset ProcessStartTimeUtc, string ExePath, int WindowsSessionId, long WindowHandle);
// No free text, UI names, input values, exception messages, or credentials in the protocol.
public sealed record EvidenceState(int? ControlTypeId = null, bool? Enabled = null, string FingerprintSha256 = null);
public sealed record EvidencePolicy(bool RecordVideo = false, string FfmpegPath = null, int MaxElements = 128, int MaxDepth = 8);
public sealed record EvidenceRequest
{
    public int SchemaVersion { get; init; } = 1;
    public Guid RequestId { get; init; }
    public DateTimeOffset DeadlineUtc { get; init; }
    public string Method { get; init; }
    public Guid SessionId { get; init; }
    public EvidenceTarget Target { get; init; }
    public string OutputDirectory { get; init; }
    public EvidencePolicy Policy { get; init; }
    public Guid StepId { get; init; }
    public Guid StepToken { get; init; }
    public string OperationName { get; init; }
    public EvidenceState ExpectedStateMetadata { get; init; }
    public EvidenceState ActualStateMetadata { get; init; }
    public string Outcome { get; init; }
    public string ErrorCode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public GroupEvidencePolicy GroupPolicy { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public WindowApproval[] WindowApprovals { get; init; }
}
public sealed record EvidenceReply
{
    public int SchemaVersion { get; init; } = 1;
    public Guid RequestId { get; init; }
    public Guid SessionId { get; init; }
    public Guid StepId { get; init; }
    public Guid StepToken { get; init; }
    public string Status { get; init; }
    public bool OperationAllowed { get; init; }
    public string Outcome { get; init; }
    public DateTimeOffset? SavedAt { get; init; }
    public string[] EvidencePaths { get; init; } = Array.Empty<string>();
    public Guid[] PendingSteps { get; init; } = Array.Empty<Guid>();
    public string[] Capabilities { get; init; } = Array.Empty<string>();
    public EvidenceStepSummary[] StepStates { get; init; } = Array.Empty<EvidenceStepSummary>();
    public bool HasMoreSteps { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public GroupQuality Quality { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public WindowGraphSnapshot WindowGraph { get; init; }
}
public sealed record EvidenceStepSummary(Guid StepId, Guid StepToken, string Status, bool BeforeSaved, bool AfterSaved, bool FailureSaved, string Outcome);
public sealed record EvidenceNode(int ControlTypeId, int X, int Y, int Width, int Height, bool IsPassword, bool Enabled);
public sealed record EvidenceUi(IReadOnlyList<EvidenceNode> Elements, bool Truncated, string VisualProtection = "opaque-all", bool TextRead = false);
public sealed record CaptureWork(Guid SessionId, Guid StepId, Guid StepToken, string Phase, EvidenceTarget Target,
    EvidencePolicy Policy, string Directory, DateTimeOffset SessionStartedUtc, DateTimeOffset DeadlineUtc, long SessionClockStartTicks = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GroupCapturePlan GroupPlan = null);
public sealed record EvidenceCommit(int SchemaVersion, Guid SessionId, Guid StepId, Guid StepToken, string Phase,
    DateTimeOffset CapturedAt, DateTimeOffset SavedAt, long TimelineOffsetMs, string ProductVersion, string ImplementationSha256,
    string ImageSha256, string UiSha256, string VideoSha256 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string GroupSha256 = null);
public interface IEvidenceBackend
{
    Task<string> CaptureAndSaveAsync(CaptureWork work, CancellationToken cancellationToken);
}
public interface IEvidenceTargetValidator
{
    bool Matches(EvidenceTarget target);
}
public sealed class EvidenceUnavailableException : Exception
{
    public string Code { get; }
    public EvidenceUnavailableException(string code) : base(code) { Code = code; }
}
