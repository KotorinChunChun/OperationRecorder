using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace OperationRecorderApp.ExternalEvidence;

public static class GroupEvidenceValidation
{
    const long MaxPngBytes = 33554432, MaxUiBytes = 65536;
    static readonly HashSet<string> RowReasons = new(StringComparer.Ordinal)
    {
        "included-in-parent", "unapproved-window", "foreign-window", "capture-unavailable", "state-unproven",
        "profile-required", "context-unproven", "invalid-profile", "application-mismatch", "protection-unproven",
        "invalid-denied-regions", "role-not-approved", "dpi-mismatch", "size-mismatch", "state-mismatch",
        "denied-overlap", "pixel-mismatch", "no-diagnostic-pixels", "requirements-unmet", "disclosure-unavailable",
        "invalid-frame", "approved", "public-approved", "none", "child-region-clipped", "window-set-changed",
        "roi-approved", "roi-scene-unproven", "roi-authentication-present", "roi-denied-overlap"
    };
    static readonly HashSet<string> QualityReasons = new(StringComparer.Ordinal)
    { "window-set-changed", "foreign-window", "unapproved-window", "required-evidence-missing", "frame-skew-or-missing", "public-diagnostic-insufficient" };
    static bool Bound(WindowApproval grant, WindowObservation window) => grant != null && window != null &&
        grant.Hwnd == window.Hwnd && grant.WindowId == window.WindowId && grant.Generation == window.Generation;
    static bool Public(WindowEvidence row) => row.CaptureStatus == "fresh" && row.RequirementsMet && row.DisclosureStatus == "public-regions";
    static bool Covered(GroupCapturePlan plan, WindowObservation child)
    {
        var grants = plan.Policy.Approvals ?? Array.Empty<WindowApproval>();
        var root = plan.Snapshot.Windows.FirstOrDefault(w => w.Relation == "root");
        if (root == null || !child.IsChild || child.Pid != root.Pid || child.Relation != "child") return false;
        var self = grants.FirstOrDefault(g => Bound(g,child));
        if (self == null || self.Role != "contained" || self.Required) return false;
        var seen = new HashSet<long>(); var cursor = child;
        while (cursor.IsChild && seen.Add(cursor.Hwnd) && seen.Count <= plan.Policy.MaxWindows)
        {
            var parent = plan.Snapshot.Windows.FirstOrDefault(w => w.Hwnd == cursor.ParentHwnd);
            if (parent == null || parent.Pid != root.Pid || parent.Relation is "foreign" or "unowned") return false;
            var grant = grants.FirstOrDefault(g => Bound(g,parent));
            if (grant == null) return false;
            if (grant.Role != "contained") return true;
            if (grant.Required) return false;
            cursor = parent;
        }
        return false;
    }
    public static GroupQuality Evaluate(GroupCapturePlan plan, IReadOnlyList<WindowEvidence> rows, bool coherent)
    {
        var reasons = new List<string>();
        var approvals = plan?.Policy?.Approvals ?? Array.Empty<WindowApproval>();
        var expected = plan?.Snapshot?.Windows;
        rows ??= Array.Empty<WindowEvidence>();
        bool validSet = expected != null && expected.Count > 0 && rows.Count == expected.Count && rows.All(w => w?.Window != null) &&
            rows.Select(w=>w.Window.Hwnd).Distinct().Count() == rows.Count &&
            rows.All(w=>expected.Any(e=>e==w.Window));
        if (!validSet) reasons.Add("window-set-changed");
        if (!coherent || plan?.Snapshot?.Truncated != false) reasons.Add("window-set-changed");
        bool foreign = rows.Any(w=>w?.Window?.Relation=="foreign");
        if (foreign) reasons.Add("foreign-window");
        bool unapproved = rows.Any(row => row?.Window == null || row.Window.Relation == "foreign" ||
            !approvals.Any(g=>Bound(g,row.Window)) ||
            (approvals.FirstOrDefault(g=>Bound(g,row.Window))?.Role == "contained" && !Covered(plan,row.Window)));
        if (unapproved) reasons.Add("unapproved-window");
        var required = approvals.Where(g=>g.Required).ToArray();
        bool roi = plan?.Policy?.RoiProfile != null;
        bool DiagnosticRow(WindowEvidence row) => Public(row) &&
            (!roi || row.ApprovedSceneId != null && row.VerifiedFrameSha256 == null);
        bool Fresh(WindowApproval grant) => rows.Any(row=>row?.Window!=null && Bound(grant,row.Window) && row.Role==grant.Role &&
            row.CaptureStatus=="fresh" && row.FrameTime100ns>0 &&
            (!(roi || plan.Policy.RequirePublicDiagnostic) || DiagnosticRow(row)));
        var missing = required.Where(g=>!Fresh(g)).Select(g=>g.WindowId).Distinct().ToArray();
        if (missing.Length>0 || required.Length==0) reasons.Add("required-evidence-missing");
        var frames = rows.Where(w=>w?.CaptureStatus=="fresh").ToArray();
        bool timed = frames.Length>0 && frames.All(w=>w.FrameTime100ns>0) && plan?.Policy!=null && plan.Policy.MaxGroupSkewMs>=0 &&
            frames.Max(w=>w.FrameTime100ns.Value)-frames.Min(w=>w.FrameTime100ns.Value)<=plan.Policy.MaxGroupSkewMs*10000L;
        if (!timed) reasons.Add("frame-skew-or-missing");
        bool visibleRoiWindowsProven = !roi || rows.All(row=>row?.Window!=null &&
            (!row.Window.Visible || row.Role=="contained" && Covered(plan,row.Window) || DiagnosticRow(row)));
        bool met = validSet && coherent && plan?.Snapshot?.Truncated==false && !foreign && !unapproved && timed &&
            required.Length>0 && missing.Length==0 && visibleRoiWindowsProven;
        bool diagnostic = met && required.All(g=>rows.Any(row=>row?.Window!=null && Bound(g,row.Window) &&
            row.Role==g.Role && DiagnosticRow(row)));
        if (!diagnostic) reasons.Add("public-diagnostic-insufficient");
        string disclosure = rows.Any(w=>w?.DisclosureStatus=="public-regions") ? "public-regions" :
            rows.Any(w=>w?.DisclosureStatus=="partial-public") ? "partial-public" : "opaque-only";
        return new(frames.Length>0?"captured":"unavailable",disclosure,diagnostic?"sufficient":"insufficient",met,
            reasons.Distinct(StringComparer.Ordinal).ToArray(),missing);
    }
    public static void Verify(CaptureWork work, GroupEvidenceManifest manifest)
    {
        try { VerifyCore(work,manifest); }
        catch { throw new EvidenceUnavailableException("save-unconfirmed"); }
    }
    static void Require(bool condition) { if (!condition) throw new EvidenceUnavailableException("save-unconfirmed"); }
    static void VerifyCore(CaptureWork work, GroupEvidenceManifest manifest)
    {
        var plan = work?.GroupPlan;
        Require(plan?.Snapshot?.Windows!=null && plan.Policy!=null && manifest?.Windows!=null && manifest.Quality!=null);
        ApprovedRoiProfile roiProfile = null;
        if (plan.Policy.RoiProfile != null)
        {
            Require(plan.Policy.Disclosure == null && plan.Policy.RequirePublicDiagnostic);
            roiProfile = ApprovedRoiCatalog.Load(plan.Policy.RoiProfile);
            Require(string.Equals(roiProfile.AppSha256, plan.AppSha256, StringComparison.OrdinalIgnoreCase));
        }
        Require(manifest.SchemaVersion==2 && manifest.SessionId==work.SessionId && manifest.StepId==work.StepId &&
            manifest.StepToken==work.StepToken && manifest.Phase==work.Phase && manifest.RegistryRevision==plan.Snapshot.Revision);
        Require(manifest.PolicySha256==EvidenceFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(plan.Policy,EvidenceProtocol.Json)));
        // Inspection reconstructs CaptureWork later. Validate the saved interval, never its age
        // relative to this process or a reconstructed session-start timestamp.
        Require(manifest.CollectionStarted<=manifest.CollectionEnded);
        Require(manifest.Windows.Length==plan.Snapshot.Windows.Count && manifest.Windows.Length>0 && manifest.Windows.Length<=plan.Policy.MaxWindows &&
            manifest.Windows.All(w=>w?.Window!=null) && manifest.Windows.Select(w=>w.Window.Hwnd).Distinct().Count()==manifest.Windows.Length &&
            manifest.Windows.Select(w=>w.Window.WindowId).Distinct().Count()==manifest.Windows.Length);
        Require(plan.Snapshot.Windows.All(w=>w!=null && w.WindowId!=Guid.Empty && w.Generation>0 && w.Hwnd!=0 && w.Pid>0) &&
            plan.Snapshot.Windows.Count(w=>w.Relation=="root")==1 && plan.Snapshot.Windows.Any(w=>w.Relation=="root" && w.Pid==work.Target.Pid && w.Hwnd==work.Target.WindowHandle));
        var approvals=plan.Policy.Approvals??Array.Empty<WindowApproval>();
        Require(approvals.Length>0 && approvals.Select(g=>g.Hwnd).Distinct().Count()==approvals.Length &&
            approvals.All(g=>plan.Snapshot.Windows.Any(w=>Bound(g,w))));
        if (roiProfile != null) Require(approvals.Any(g=>g.Hwnd==work.Target.WindowHandle && g.Required));
        EvidenceFiles.RequireLocalUnlinkedPath(work.Directory);
        foreach(var row in manifest.Windows)
        {
            Require(plan.Snapshot.Windows.Any(w=>w==row.Window));
            var grant=approvals.FirstOrDefault(g=>Bound(g,row.Window));
            bool contained=grant?.Role=="contained" && Covered(plan,row.Window);
            bool unknown=grant==null || row.Window.Relation=="foreign";
            Require(row.CaptureMethod is "wgc-window" or "wgc-parent-crop" or "structure-only");
            Require(row.CaptureStatus is "fresh" or "stale-frame" or "capture-unavailable" or "not-captured");
            Require(row.DisclosureStatus is "public-regions" or "partial-public" or "opaque-only" or "blocked");
            Require(RowReasons.Contains(row.ReasonCode??""));
            if (roiProfile == null)
                Require(row.ApprovedSceneId == null);
            else
            {
                bool published = row.DisclosureStatus == "public-regions";
                Require(row.VerifiedFrameSha256 == null && row.DisclosureStatus != "partial-public" &&
                    published == row.RequirementsMet && published == (row.ApprovedSceneId != null) &&
                    published == (row.ReasonCode == "roi-approved"));
                Require(!published || row.Window.Visible && !row.Window.Minimized && !row.Window.Cloaked);
            }
            if(unknown || grant?.Role=="contained")
            {
                Require(row.Role==(contained?"contained":"unknown") && row.CaptureMethod=="structure-only" &&
                    row.CaptureStatus=="not-captured" && row.DisclosureStatus=="opaque-only" && !row.RequirementsMet &&
                    row.ImageFile==null && row.UiFile==null && row.ImageSha256==null && row.UiSha256==null &&
                    row.FrameTime100ns==null && row.SourceHwnd==null && row.Width==0 && row.Height==0);
                Require(contained?row.ReasonCode=="included-in-parent":row.ReasonCode is "unapproved-window" or "foreign-window");
                continue;
            }
            Require(row.Role==grant.Role && row.CaptureMethod!="structure-only");
            if(row.CaptureMethod=="wgc-window") Require(row.SourceHwnd==null);
            if(row.CaptureMethod=="wgc-parent-crop")
            {
                var parent=plan.Snapshot.Windows.FirstOrDefault(w=>w.Hwnd==row.Window.ParentHwnd);
                Require(row.Window.IsChild && row.Window.Relation=="child" && grant.AllowParentCrop && row.SourceHwnd==row.Window.ParentHwnd &&
                    parent!=null && parent.Pid==row.Window.Pid && parent.Relation!="foreign" && approvals.Any(g=>Bound(g,parent) && g.Role!="contained") &&
                    row.Window.Bounds.Width>0 && row.Window.Bounds.Height>0 && parent.Bounds.Contains(row.Window.Bounds));
            }
            if(row.CaptureStatus=="capture-unavailable")
            {
                Require(row.ImageFile==null && row.UiFile==null && row.ImageSha256==null && row.UiSha256==null &&
                    !row.RequirementsMet && row.DisclosureStatus=="blocked" && row.FrameTime100ns==null && row.Width==0 && row.Height==0);
                continue;
            }
            Require(row.CaptureStatus is "fresh" or "stale-frame");
            Require(row.Width>0 && row.Height>0 && row.Width<=4096 && row.Height<=4096 && (long)row.Width*row.Height<=4194304 && row.FrameTime100ns>0);
            Require(!row.RequirementsMet || row.CaptureStatus=="fresh" && row.DisclosureStatus=="public-regions");
            Require(row.CaptureStatus=="fresh" || row.DisclosureStatus is "opaque-only" or "blocked");
            string prefix="windows/"+row.Window.WindowId.ToString("N")+"/";
            Require(row.ImageFile==prefix+"frame.png" && row.UiFile==prefix+"ui.json");
            byte[] png=Read(work.Directory,row.ImageFile,row.ImageSha256,MaxPngBytes);
            byte[] ui=Read(work.Directory,row.UiFile,row.UiSha256,MaxUiBytes);
            Require(png.Length>=33 && png.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}) &&
                png.AsSpan(12,4).SequenceEqual(new byte[]{73,72,68,82}) && BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16,4))==row.Width &&
                BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20,4))==row.Height);
            var parsed=SafeUi(ui);
            using(var stream=new MemoryStream(png))using(var image=new Bitmap(stream))
            {
                Require(image.Width==row.Width && image.Height==row.Height);
                if (roiProfile != null && row.DisclosureStatus == "public-regions")
                {
                    Require(row.Window.Bounds.Size == image.Size &&
                        ApprovedRoiGate.VerifyProtected(image, roiProfile, row.ApprovedSceneId, row.Role,
                            work.Phase, row.Window.Dpi, parsed));
                }
                else VerifyPixels(plan,row,parsed,image);
            }
        }
        var rootRow=manifest.Windows.Single(row=>row.Window.Hwnd==work.Target.WindowHandle);
        string flatImage=Path.Combine(work.Directory,"frame.png"),flatUi=Path.Combine(work.Directory,"ui.json");
        if (roiProfile != null && rootRow.DisclosureStatus=="public-regions")
            Require(File.Exists(flatImage));
        if(File.Exists(flatImage))
        {
            byte[] png=ReadBounded(work.Directory,"frame.png",MaxPngBytes);
            // The compatibility image must be the already verified root image or an opaque placeholder.
            if(rootRow.ImageSha256==null || !string.Equals(EvidenceFiles.Hash(png),rootRow.ImageSha256,StringComparison.OrdinalIgnoreCase))
            {
                Require(roiProfile == null || rootRow.DisclosureStatus != "public-regions");
                Require(png.Length>=33 && png.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}) &&
                    png.AsSpan(12,4).SequenceEqual(new byte[]{73,72,68,82}));
                int width=BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16,4)),height=BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20,4));
                Require(width>0 && height>0 && width<=4096 && height<=4096 && (long)width*height<=4194304);
                using var stream=new MemoryStream(png);using var image=new Bitmap(stream);
                Require(image.Width==width && image.Height==height);
                VerifyPixels(plan,rootRow with{DisclosureStatus="opaque-only"},new EvidenceUi(Array.Empty<EvidenceNode>(),false),image);
            }
        }
        if(File.Exists(flatUi))SafeUi(ReadBounded(work.Directory,"ui.json",MaxUiBytes));
        var evaluated=Evaluate(plan,manifest.Windows,true);
        Require(!manifest.Quality.RequirementsMet || evaluated.RequirementsMet);
        Require(manifest.Quality.DiagnosticStatus!="sufficient" || evaluated.DiagnosticStatus=="sufficient");
        Require(manifest.Quality.DiagnosticStatus!="sufficient" || manifest.Quality.RequirementsMet);
        Require(manifest.Quality.DiagnosticStatus is "sufficient" or "insufficient");
        Require(manifest.Quality.CaptureStatus is "captured" or "unavailable" &&
            manifest.Quality.DisclosureStatus is "public-regions" or "partial-public" or "opaque-only" or "blocked");
        Require(manifest.Quality.Reasons!=null && manifest.Quality.Reasons.All(reason=>QualityReasons.Contains(reason??"")) && manifest.Quality.MissingWindows!=null &&
            manifest.Quality.MissingWindows.All(id=>approvals.Any(g=>g.Required && g.WindowId==id)));
        if (roiProfile != null)
        {
            bool met = manifest.Quality.RequirementsMet;
            bool anyPublic = manifest.Windows.Any(row=>row.DisclosureStatus=="public-regions");
            Require(met == (manifest.Quality.DiagnosticStatus=="sufficient") && met == anyPublic &&
                manifest.Quality.CaptureStatus == evaluated.CaptureStatus &&
                manifest.Quality.DisclosureStatus == evaluated.DisclosureStatus &&
                manifest.Quality.MissingWindows.Order().SequenceEqual(evaluated.MissingWindows.Order()));
        }
    }
    static byte[] Read(string directory,string relative,string hash,long maximum)
    {
        Require(EvidenceProtocol.IsHash(hash));
        byte[] bytes=ReadBounded(directory,relative,maximum);
        Require(string.Equals(EvidenceFiles.Hash(bytes),hash,StringComparison.OrdinalIgnoreCase));
        return bytes;
    }
    static byte[] ReadBounded(string directory,string relative,long maximum)
    {
        string path=Path.Combine(directory,relative.Replace('/',Path.DirectorySeparatorChar));
        EvidenceFiles.RequireLocalUnlinkedPath(path);
        var info=new FileInfo(path); Require(info.Exists && info.Length>0 && info.Length<=maximum);
        byte[] bytes=File.ReadAllBytes(path);Require(bytes.Length>0 && bytes.Length<=maximum);
        return bytes;
    }
    static EvidenceUi SafeUi(byte[] bytes)
    {
        var parsed=JsonSerializer.Deserialize<EvidenceUi>(bytes,EvidenceProtocol.Json);
        Require(parsed!=null && !parsed.TextRead && parsed.Elements!=null && parsed.Elements.Count<=256 &&
            parsed.VisualProtection is "opaque-all" or "public-regions" or "partial-public" or "blocked");
        return parsed;
    }
    static void VerifyPixels(GroupCapturePlan plan,WindowEvidence row,EvidenceUi ui,Bitmap image)
    {
        bool published=row.DisclosureStatus is "public-regions" or "partial-public";
        var allowed=new bool[checked(image.Width*image.Height)];
        if(published)
        {
            var profile=plan.Policy.Disclosure;
            Require(profile!=null && profile.SchemaVersion==1 && string.Equals(profile.AppSha256,plan.AppSha256,StringComparison.OrdinalIgnoreCase) &&
                EvidenceProtocol.IsHash(row.VerifiedFrameSha256) && profile.ApprovedFrameSha256?.Any(hash=>string.Equals(hash,row.VerifiedFrameSha256,StringComparison.OrdinalIgnoreCase))==true &&
                profile.Rules!=null && profile.Rules.Length<=64 && !ui.Truncated && row.Window.Bounds.Size==new Size(row.Width,row.Height) && row.Window.Dpi>0);
            var rules=profile.Rules.Where(rule=>rule!=null && rule.Role==row.Role).ToArray();
            Require(rules.Length>0);
            var denied=ui.Elements.Where(e=>e.IsPassword||e.ControlTypeId is 50004 or 50025)
                .Select(e=>Rectangle.Inflate(new(e.X,e.Y,e.Width,e.Height),2,2)).ToArray();
            int matched=0;
            foreach(var rule in rules)
            {
                Require(rule.Dpi==row.Window.Dpi && rule.WindowWidth==row.Width && rule.WindowHeight==row.Height && rule.Region.X>=0 && rule.Region.Y>=0 &&
                    rule.Region.Width>0 && rule.Region.Height>0 && (long)rule.Region.X+rule.Region.Width<=row.Width && (long)rule.Region.Y+rule.Region.Height<=row.Height);
                string hash=DisclosureGate.PixelHash(image,rule.Region);
                if(rule.ApprovedPixelSha256?.Any(approved=>string.Equals(approved,hash,StringComparison.OrdinalIgnoreCase))!=true || denied.Any(area=>area.IntersectsWith(rule.Region)))continue;
                matched++;
                for(int y=rule.Region.Top;y<rule.Region.Bottom;y++)for(int x=rule.Region.Left;x<rule.Region.Right;x++)allowed[y*row.Width+x]=true;
            }
            Require(matched>0 && (!row.RequirementsMet || matched==rules.Length));
        }
        BitmapData data=null;
        try
        {
            data=image.LockBits(new Rectangle(0,0,image.Width,image.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
            byte[] pixels=new byte[checked(image.Width*4)];bool nonBlackPublic=false;
            for(int y=0;y<image.Height;y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),pixels,0,pixels.Length);
                for(int x=0;x<image.Width;x++)
                {
                    int i=x*4;Require(pixels[i+3]==255);
                    bool nonBlack=pixels[i]!=0||pixels[i+1]!=0||pixels[i+2]!=0;
                    Require(!nonBlack||allowed[y*image.Width+x]);
                    if(nonBlack)nonBlackPublic=true;
                }
            }
            Require(!published||nonBlackPublic);
        }
        finally{if(data!=null)image.UnlockBits(data);}
    }
}
