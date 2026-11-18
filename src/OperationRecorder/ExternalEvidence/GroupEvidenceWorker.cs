using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace OperationRecorderApp.ExternalEvidence;

public static class GroupEvidenceWorker
{
    public static async Task<int> RunAsync(CaptureWork work, Stream output, CancellationToken outer)
    {
        WorkerReply reply;
        try { await CaptureAsync(work,outer); reply=new("saved"); }
        catch(OperationCanceledException){reply=new("deadline-exceeded");}
        catch(EvidenceUnavailableException e){reply=new(e.Code is "target-mismatch" or "save-unavailable" or "deadline-exceeded" ? e.Code : "capture-unavailable");}
        catch{reply=new("capture-unavailable");}
        await EvidenceWire.WriteAsync(output,reply,CancellationToken.None); return reply.Status=="saved"?0:1;
    }
    public static async Task CaptureAsync(CaptureWork work,CancellationToken outer)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(outer);
        var remaining=work.DeadlineUtc-DateTimeOffset.UtcNow;
        if(remaining<=TimeSpan.Zero || remaining>TimeSpan.FromSeconds(30))throw new EvidenceUnavailableException("deadline-exceeded");
        deadline.CancelAfter(remaining);var token=deadline.Token;
        var plan=work.GroupPlan??throw new EvidenceUnavailableException("invalid-request");
        using var registry=new WindowGraphRegistry();
        if(!registry.MatchesProcess(work.Target))throw new EvidenceUnavailableException("target-mismatch");
        var first=registry.Snapshot(work.Target,plan.Policy.MaxWindows);
        bool coherent=!first.Truncated && GroupEvidenceContract.Fingerprint(first)==GroupEvidenceContract.Fingerprint(plan.Snapshot);
        if(EvidenceFiles.HashFile(work.Target.ExePath)!=plan.AppSha256)throw new EvidenceUnavailableException("target-mismatch");
        var started=DateTimeOffset.UtcNow;var results=new List<WindowEvidence>();
        var rawFrames=new Dictionary<long,WgcCapturedFrame>(); var capture=new WgcCaptureBackend();
        var grants=plan.Policy.Approvals??Array.Empty<WindowApproval>();
        try
        {
            foreach(var window in plan.Snapshot.Windows.OrderBy(w=>w.Hwnd==work.Target.WindowHandle?-1:w.IsChild?1:0))
            {
                token.ThrowIfCancellationRequested();
                var grant=grants.FirstOrDefault(g=>g.Hwnd==window.Hwnd);
                if(grant==null || window.Relation=="foreign" || grant.Role=="contained")
                {
                    bool contained=grant?.Role=="contained" && window.Relation!="foreign" && GroupEvidenceContract.CoveredChild(window,plan.Snapshot,grants);
                    results.Add(new(window,contained?"contained":"unknown","structure-only",null,null,0,0,null,null,null,null,
                        "not-captured","opaque-only",false,contained?"included-in-parent":"unapproved-window"));continue;
                }
                WgcCapturedFrame frame=null;string method="wgc-window";long? source=null; Bitmap crop=null;Bitmap bitmap=null;
                try
                {
                    if(window.Cloaked)throw new EvidenceUnavailableException("capture-unavailable");
                    try
                    {
                        frame=await capture.CaptureAsync(window.Hwnd,work.DeadlineUtc<DateTimeOffset.UtcNow.AddSeconds(3)?work.DeadlineUtc:DateTimeOffset.UtcNow.AddSeconds(3),token);
                        if(rawFrames.Values.Sum(f=>(long)f.ContentSize.Width*f.ContentSize.Height)+(long)frame.ContentSize.Width*frame.ContentSize.Height>plan.Policy.MaxTotalPixels)
                        { frame.Dispose();frame=null;throw new EvidenceUnavailableException("pixel-budget-exceeded"); }
                        rawFrames[window.Hwnd]=frame;bitmap=frame.Bitmap;
                    }
                    catch(EvidenceUnavailableException) when(window.IsChild && grant.AllowParentCrop && rawFrames.ContainsKey(window.ParentHwnd))
                    {
                        frame=rawFrames[window.ParentHwnd];
                        var parent=plan.Snapshot.Windows.First(w=>w.Hwnd==window.ParentHwnd);
                        var rectangle=new Rectangle(window.Bounds.X-parent.Bounds.X,window.Bounds.Y-parent.Bounds.Y,window.Bounds.Width,window.Bounds.Height);
                        if(parent.Bounds.Size!=frame.ContentSize || rectangle.Width<=0 || rectangle.Height<=0 || !new Rectangle(Point.Empty,frame.ContentSize).Contains(rectangle))
                            throw new EvidenceUnavailableException("child-region-clipped");
                        crop=frame.Bitmap.Clone(rectangle,PixelFormat.Format32bppPArgb);bitmap=crop;method="wgc-parent-crop";source=parent.Hwnd;
                    }
                    long age=WgcCaptureBackend.Clock100ns()-frame.FrameTime100ns;
                    bool fresh=age>=0 && age<=plan.Policy.MaxFrameAgeMs*10000L;
                    var area=window.Bounds;
                    var ui=ProtectedEvidenceUi.Collect(new UiaEvidenceElement(AutomationElement.FromHandle(new IntPtr(window.Hwnd))),work.Target.Pid,area,work.Policy,token);
                    var denied=ui.Elements.Where(e=>e.IsPassword || e.ControlTypeId is 50004 or 50025)
                        .Select(e=>Rectangle.Intersect(new Rectangle(Point.Empty,bitmap.Size),Rectangle.Inflate(new Rectangle(e.X,e.Y,e.Width,e.Height),2,2))).Where(r=>r.Width>0&&r.Height>0).ToArray();
                    var after=registry.Snapshot(work.Target,plan.Policy.MaxWindows);
                    bool context=coherent && fresh && !ui.Truncated && area.Size==bitmap.Size && !after.Truncated && GroupEvidenceContract.Fingerprint(after)==GroupEvidenceContract.Fingerprint(first);
                    var decision=DisclosureGate.Apply(bitmap,plan.AppSha256,grant.Role,window.Dpi,plan.Policy.Disclosure,denied,context);
                    string relative="windows/"+window.WindowId.ToString("N");
                    string image=relative+"/frame.png",json=relative+"/ui.json";
                    EvidenceFiles.AtomicBytes(Path.Combine(work.Directory,image),decision.Png);
                    EvidenceFiles.AtomicJson(Path.Combine(work.Directory,json),ui);
                    results.Add(new(window,grant.Role,method,source,frame.FrameTime100ns,bitmap.Width,bitmap.Height,image,
                        EvidenceFiles.HashFile(Path.Combine(work.Directory,image)),json,EvidenceFiles.HashFile(Path.Combine(work.Directory,json)),
                        fresh?"fresh":"stale-frame",decision.DisclosureStatus,decision.RequirementsMet,context?decision.ReasonCode:"state-unproven",decision.VerifiedFrameSha256));
                }
                catch(OperationCanceledException){throw;}
                catch
                {
                    results.Add(new(window,grant.Role,method,source,null,0,0,null,null,null,null,"capture-unavailable","blocked",false,"capture-unavailable"));
                }
                finally{crop?.Dispose();}
            }
            var last=registry.Snapshot(work.Target,plan.Policy.MaxWindows);
            coherent &= !last.Truncated && GroupEvidenceContract.Fingerprint(last)==GroupEvidenceContract.Fingerprint(first)
                && EvidenceFiles.HashFile(work.Target.ExePath)==plan.AppSha256;
            if(!coherent)
            {
                for(int i=0;i<results.Count;i++)
                {
                    var row=results[i];if(row.ImageFile==null)continue;
                    var black=EvidenceFiles.OpaquePng(new Size(row.Width,row.Height));EvidenceFiles.AtomicBytes(Path.Combine(work.Directory,row.ImageFile),black);
                    results[i]=row with{ImageSha256=EvidenceFiles.Hash(black),DisclosureStatus="blocked",RequirementsMet=false,ReasonCode="window-set-changed"};
                }
            }
            var quality=GroupEvidenceValidation.Evaluate(plan,results,coherent);
            var manifest=new GroupEvidenceManifest(2,work.SessionId,work.StepId,work.StepToken,work.Phase,plan.Snapshot.Revision,
                EvidenceFiles.Hash(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(plan.Policy,EvidenceProtocol.Json)),started,DateTimeOffset.UtcNow,results.ToArray(),quality);
            EvidenceFiles.AtomicJson(Path.Combine(work.Directory,"group.json"),manifest);
            var root=results.FirstOrDefault(r=>r.Window.Hwnd==work.Target.WindowHandle && r.ImageFile!=null);
            byte[] main=root==null?EvidenceFiles.OpaquePng(new Size(16,16)):File.ReadAllBytes(Path.Combine(work.Directory,root.ImageFile));
            EvidenceFiles.Save(work,main,new EvidenceUi(Array.Empty<EvidenceNode>(),false),DateTimeOffset.UtcNow);
        }
        finally{foreach(var f in rawFrames.Values.Distinct())f.Dispose();}
    }
}
