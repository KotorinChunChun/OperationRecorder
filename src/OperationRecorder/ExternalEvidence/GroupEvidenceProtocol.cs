using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OperationRecorderApp.ExternalEvidence;

public sealed record WindowApproval(long Hwnd, string Role, bool Required = true, Guid WindowId = default, int Generation = 0, bool AllowParentCrop = true);
public sealed record GroupEvidencePolicy(WindowApproval[] Approvals = null, DisclosureProfile Disclosure = null,
    bool RequirePublicDiagnostic = true, int MaxWindows = 16, int MaxFrameAgeMs = 3000, int MaxGroupSkewMs = 2000, int MaxTotalPixels = 8388608,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RoiProfileSelection RoiProfile = null);
public sealed record GroupCapturePlan(WindowGraphSnapshot Snapshot, GroupEvidencePolicy Policy, string AppSha256);
public sealed record GroupQuality(string CaptureStatus, string DisclosureStatus, string DiagnosticStatus, bool RequirementsMet,
    string[] Reasons, Guid[] MissingWindows);
public sealed record WindowEvidence(WindowObservation Window, string Role, string CaptureMethod, long? SourceHwnd,
    long? FrameTime100ns, int Width, int Height, string ImageFile, string ImageSha256, string UiFile, string UiSha256,
    string CaptureStatus, string DisclosureStatus, bool RequirementsMet, string ReasonCode, string VerifiedFrameSha256 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string ApprovedSceneId = null);
public sealed record GroupEvidenceManifest(int SchemaVersion, Guid SessionId, Guid StepId, Guid StepToken, string Phase,
    long RegistryRevision, string PolicySha256, DateTimeOffset CollectionStarted, DateTimeOffset CollectionEnded,
    WindowEvidence[] Windows, GroupQuality Quality);
public static class GroupEvidenceContract
{
    public static string Fingerprint(WindowGraphSnapshot snapshot) => EvidenceFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(
        new { snapshot.Truncated, windows = snapshot.Windows.OrderBy(w=>w.Hwnd).Select(w=> new { w.Hwnd,w.ParentHwnd,w.OwnerHwnd,w.Pid,w.IsChild,w.Visible,w.Minimized,w.Cloaked,w.Bounds,w.Dpi,w.Relation }) },EvidenceProtocol.Json));
    public static bool CoveredChild(WindowObservation window, WindowGraphSnapshot snapshot, IReadOnlyList<WindowApproval> grants)
    {
        var cursor=window; var visited=new HashSet<long>();
        while(cursor.IsChild && visited.Add(cursor.Hwnd))
        {
            if(grants.Any(g=>g.Hwnd==cursor.ParentHwnd))return true;
            cursor=snapshot.Windows.FirstOrDefault(w=>w.Hwnd==cursor.ParentHwnd);
            if(cursor==null)return false;
        }
        return false;
    }
}
