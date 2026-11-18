using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OperationRecorderApp.ExternalEvidence;

public sealed class GroupEvidenceSession : IDisposable
{
    readonly IEvidenceBackend backend;
    readonly WindowGraphRegistry registry;
    readonly Func<EvidenceTarget,int,WindowGraphSnapshot> snapshot;
    readonly SemaphoreSlim gate=new(1,1);
    readonly Dictionary<Guid,EvidenceStep> steps=new();
    readonly Dictionary<Guid,string> requests=new();
    Guid id; EvidenceTarget target; GroupEvidencePolicy policy; string directory,appHash; DateTimeOffset started; FileStream fileLock; bool stopped;
    public GroupEvidenceSession(IEvidenceBackend backend,Func<EvidenceTarget,int,WindowGraphSnapshot> snapshot=null)
    {
        this.backend=backend;
        if(snapshot==null){registry=new WindowGraphRegistry();this.snapshot=registry.Snapshot;}
        else this.snapshot=snapshot;
    }
    public async Task<EvidenceReply> HandleAsync(EvidenceRequest request,CancellationToken cancellation=default)
    {
        EvidenceReply Reply(string status,EvidenceStep step=null,GroupQuality quality=null,string[] paths=null)=>new()
        {SchemaVersion=2,RequestId=request?.RequestId??Guid.Empty,SessionId=id,StepId=step?.StepId??request?.StepId??Guid.Empty,
            StepToken=step?.Token??Guid.Empty,Status=status,Outcome=step?.Outcome,Quality=quality,EvidencePaths=paths??Array.Empty<string>(),
            PendingSteps=steps.Values.Where(s=>s.Status!="completed"||s.Outcome=="unknown").Select(s=>s.StepId).ToArray()};
        bool entered=false;EvidenceStep current=null;
        try
        {
            if(request==null||request.SchemaVersion!=2||request.RequestId==Guid.Empty||request.SessionId==Guid.Empty||request.DeadlineUtc<=DateTimeOffset.UtcNow||request.DeadlineUtc>DateTimeOffset.UtcNow.AddSeconds(30))return Reply("invalid-request");
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(request.DeadlineUtc-DateTimeOffset.UtcNow);
            await gate.WaitAsync(deadline.Token);entered=true;
            string hash=EvidenceFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(request,EvidenceProtocol.Json));
            if(requests.TryGetValue(request.RequestId,out var previous))return Reply(previous==hash?"duplicate-request":"request-conflict");
            if(requests.Count>=20000 && request.Method is not("EndStep" or "RecordFailure" or "Flush" or "StopSession" or "InspectSession"))return Reply("session-limit");
            if(requests.Count<20000)requests.Add(request.RequestId,hash);
            if(request.Method=="InspectSession")return Inspect(request);
            if(request.Method=="StartSession")
            {
                if(id!=Guid.Empty)return Reply("already-started");
                var selected=JsonSerializer.Deserialize<GroupEvidencePolicy>(JsonSerializer.SerializeToUtf8Bytes(request.GroupPolicy??new(),EvidenceProtocol.Json),EvidenceProtocol.Json);
                if(request.Target==null||request.Policy?.RecordVideo==true||selected.MaxWindows is <1 or >32 || selected.MaxFrameAgeMs is <100 or >10000 || selected.MaxGroupSkewMs is <1 or >10000 || selected.MaxTotalPixels is <1 or >16777216)return Reply("unsupported-policy");
                if(JsonSerializer.SerializeToUtf8Bytes(selected,EvidenceProtocol.Json).Length>16384 || selected.Disclosure!=null&&!DisclosureGate.IsValidProfile(selected.Disclosure))return Reply("invalid-profile");
                var windows=snapshot(request.Target,selected.MaxWindows);
                if(windows.Truncated)return Reply("window-observation-unavailable");
                target=request.Target;appHash=EvidenceFiles.HashFile(target.ExePath);
                if(selected.Disclosure!=null && !string.Equals(selected.Disclosure.AppSha256,appHash,StringComparison.OrdinalIgnoreCase))return Reply("profile-app-mismatch");
                var approvals=Bind(selected.Approvals??new[]{new WindowApproval(target.WindowHandle,"root")},windows,true).ToList();
                foreach(var child in windows.Windows.Where(w=>w.IsChild&&w.Relation!="foreign"&&w.Pid==target.Pid))
                    if(!approvals.Any(a=>a.Hwnd==child.Hwnd)&&GroupEvidenceContract.CoveredChild(child,windows,approvals))approvals.Add(new(child.Hwnd,"contained",false,child.WindowId,child.Generation,false));
                policy=selected with{Approvals=approvals.ToArray()};
                string root=Path.GetFullPath(request.OutputDirectory??throw new EvidenceUnavailableException("invalid-output"));EvidenceFiles.RequireLocalUnlinkedPath(root);
                if(Directory.Exists(root))return Reply("output-exists");
                Directory.CreateDirectory(root);var security=new DirectorySecurity();security.SetAccessRuleProtection(true,false);
                security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));new DirectoryInfo(root).SetAccessControl(security);
                fileLock=new FileStream(Path.Combine(root,"external.lock"),FileMode.CreateNew,FileAccess.Write,FileShare.None);
                id=request.SessionId;directory=root;started=DateTimeOffset.UtcNow;
                EvidenceFiles.AtomicJson(Path.Combine(root,"external-session.json"),new{schemaVersion=2,product="operation-recorder-evidence-group",sessionId=id,target,appHash,policy,started});
                return Reply("started") with{WindowGraph=windows,Capabilities=new[]{"wgc-required","window-groups","explicit-window-approval","approved-pixel-png","opaque-default","geometry-only","quality-gated","snapshot-only"}};
            }
            if(id==Guid.Empty||id!=request.SessionId)return Reply("session-mismatch");
            if(request.GroupPolicy!=null)return Reply("policy-fixed-for-session");
            if(request.Method is "Flush" or "StopSession")
            {
                if(request.Method=="StopSession")stopped=true;
                EvidenceFiles.AtomicJson(Path.Combine(directory,"checkpoint.json"),new{schemaVersion=2,sessionId=id,stopped,steps=steps.Values.ToArray()});
                return Reply(stopped?"stopped":"flushed",paths:new[]{Path.Combine(directory,"checkpoint.json")}) with{SavedAt=DateTimeOffset.UtcNow};
            }
            if(stopped)return Reply("session-stopped");
            if(!ValidState(request.ExpectedStateMetadata)||!ValidState(request.ActualStateMetadata))return Reply("invalid-metadata");
            var observed=snapshot(target,policy.MaxWindows);
            if(request.Method=="ListWindows")return Reply("observed") with{WindowGraph=observed};
            if(request.Method=="ApproveWindows")
            {
                var additions=Bind(request.WindowApprovals??Array.Empty<WindowApproval>(),observed,false);
                if(additions.Any(a=>policy.Approvals.Any(old=>old.Hwnd==a.Hwnd && old!=a)))return Reply("approval-change-requires-new-session");
                var combined=policy.Approvals.Where(a=>!additions.Any(b=>b.Hwnd==a.Hwnd)).Concat(additions).ToArray();
                if(combined.Length>policy.MaxWindows)return Reply("window-limit");
                policy=policy with{Approvals=combined};EvidenceFiles.AtomicJson(Path.Combine(directory,"approvals.json"),policy);
                return Reply("windows-approved") with{WindowGraph=observed};
            }
            if(request.Method=="BeginStep")
            {
                if(request.StepId==Guid.Empty||!new[]{"invoke","set-value","native-message","select","observe","custom"}.Contains(request.OperationName))return Reply("invalid-request");
                if(steps.TryGetValue(request.StepId,out var duplicate))return Reply("duplicate-step",duplicate);
                if(steps.Count>=10000)return Reply("session-limit");
                if(steps.Values.Any(s=>s.Status!="completed"||s.Outcome=="unknown"))return Reply("pending-step");
                current=new(){StepId=request.StepId,Token=Guid.NewGuid(),Sequence=steps.Count+1,OperationName=request.OperationName,Expected=request.ExpectedStateMetadata};steps.Add(current.StepId,current);Save(current);
                var result=await Capture(current,"before",request,observed,deadline.Token);
                current.Before=CommitPath(current,"before");current.Status=result.Quality.RequirementsMet?"before-saved":"before-inadequate";Save(current);
                var final=FinalizeFreshness(current,"before",request,result);
                if(!ReferenceEquals(final,result)){result=final;current.Status="before-inadequate";Save(current);}
                deadline.Token.ThrowIfCancellationRequested();
                return Reply(current.Status,current,result.Quality,new[]{current.Before}) with{OperationAllowed=result.Quality.RequirementsMet,SavedAt=DateTimeOffset.UtcNow};
            }
            current=request.Method=="RecordFailure"?steps.GetValueOrDefault(request.StepId):steps.Values.FirstOrDefault(s=>s.Token==request.StepToken&&request.StepToken!=Guid.Empty);
            if(current==null)return Reply("unknown-step");
            if(request.Method=="EndStep")
            {
                if(current.Status=="completed")return Reply("duplicate-step",current);
                if(current.Status!="before-saved")return Reply("step-indeterminate",current);
                if(request.Outcome is not("success" or "failure" or "unknown"))return Reply("invalid-request",current);
                current.Status="after-unconfirmed";current.Outcome=request.Outcome;current.Actual=request.ActualStateMetadata;Save(current);
                var result=await Capture(current,"after",request,observed,deadline.Token);
                current.After=CommitPath(current,"after");current.Status=result.Quality.RequirementsMet?"completed":"after-inadequate";Save(current);
                var final=FinalizeFreshness(current,"after",request,result);
                if(!ReferenceEquals(final,result)){result=final;current.Status="after-inadequate";Save(current);}
                deadline.Token.ThrowIfCancellationRequested();
                return Reply(result.Quality.RequirementsMet?"after-saved":"after-inadequate",current,result.Quality,new[]{current.After}) with{SavedAt=DateTimeOffset.UtcNow};
            }
            if(request.Method=="RecordFailure")
            {
                if(!new[]{"unexpected-state","timeout","capture-unavailable","save-unavailable","caller-exit","operation-failed"}.Contains(request.ErrorCode))return Reply("invalid-request",current);
                if(Directory.Exists(Path.GetDirectoryName(CommitPath(current,"failure"))))return Reply("duplicate-step",current);
                EvidenceFiles.AtomicJson(Path.Combine(directory,"steps",current.StepId.ToString("N"),"failure-request.json"),new{schemaVersion=2,request.ErrorCode});
                var result=await Capture(current,"failure",request,observed,deadline.Token);current.Failure=CommitPath(current,"failure");Save(current);
                result=FinalizeFreshness(current,"failure",request,result);
                deadline.Token.ThrowIfCancellationRequested();
                return Reply("failure-saved",current,result.Quality,new[]{current.Failure}) with{SavedAt=DateTimeOffset.UtcNow};
            }
            return Reply("invalid-method",current);
        }
        catch(OperationCanceledException){return Reply(cancellation.IsCancellationRequested?"caller-unavailable":"deadline-exceeded",current);}
        catch(EvidenceUnavailableException e){return Reply(e.Code,current);}
        catch{return Reply("save-unavailable",current);}
        finally{if(entered)gate.Release();}
    }
    WindowApproval[] Bind(WindowApproval[] approvals,WindowGraphSnapshot graph,bool initial)
    {
        if(graph.Truncated||approvals.Length==0||approvals.Length>32||approvals.Select(a=>a.Hwnd).Distinct().Count()!=approvals.Length)throw new EvidenceUnavailableException("invalid-window-approval");
        return approvals.Select(a=>
        {
            var w=graph.Windows.FirstOrDefault(w=>w.Hwnd==a.Hwnd);
            if(w==null||w.Pid!=target.Pid||w.Relation=="foreign"||string.IsNullOrEmpty(a.Role)||!System.Text.RegularExpressions.Regex.IsMatch(a.Role,"\\A[a-z][a-z0-9-]{0,31}\\z")||
                (a.Role=="contained"&&(!w.IsChild||a.Required))||(!initial&&(a.WindowId!=w.WindowId||a.Generation!=w.Generation)))throw new EvidenceUnavailableException("window-approval-mismatch");
            return a with{WindowId=w.WindowId,Generation=w.Generation};
        }).ToArray();
    }
    string CommitPath(EvidenceStep step,string phase)=>Path.Combine(directory,"steps",step.StepId.ToString("N"),phase,"commit.json");
    void Save(EvidenceStep step)=>EvidenceFiles.AtomicJson(Path.Combine(directory,"steps",step.StepId.ToString("N"),"step.json"),step);
    async Task<GroupEvidenceManifest> Capture(EvidenceStep step,string phase,EvidenceRequest request,WindowGraphSnapshot graph,CancellationToken token)
    {
        foreach(var a in policy.Approvals)
            if(!graph.Windows.Any(w=>w.Hwnd==a.Hwnd&&w.WindowId==a.WindowId&&w.Generation==a.Generation))throw new EvidenceUnavailableException("window-generation-mismatch");
        string path=Path.GetDirectoryName(CommitPath(step,phase));Directory.CreateDirectory(path);
        EvidenceFiles.AtomicJson(Path.Combine(path,"started.json"),new{schemaVersion=2,request.RequestId,step.StepId,step.Token,phase});
        var work=new CaptureWork(id,step.StepId,step.Token,phase,target,new(),path,started,request.DeadlineUtc,GroupPlan:new(graph,policy,appHash));
        EvidenceFiles.AtomicJson(Path.Combine(path,"plan.json"),work.GroupPlan);
        await backend.CaptureAndSaveAsync(work,token).WaitAsync(token);token.ThrowIfCancellationRequested();EvidenceFiles.Verify(work);
        var result=JsonSerializer.Deserialize<GroupEvidenceManifest>(File.ReadAllBytes(Path.Combine(path,"group.json")),EvidenceProtocol.Json);
        GroupEvidenceValidation.Verify(work,result);
        var after=snapshot(target,policy.MaxWindows);
        if(GroupEvidenceContract.Fingerprint(after)!=GroupEvidenceContract.Fingerprint(graph)||after.Truncated||policy.Approvals.Any(a=>!after.Windows.Any(w=>w.Hwnd==a.Hwnd&&w.WindowId==a.WindowId&&w.Generation==a.Generation)))
        {
            var rows=result.Windows.Select(row=>
            {
                if(row.ImageFile==null)return row;
                var black=EvidenceFiles.OpaquePng(new System.Drawing.Size(row.Width,row.Height));EvidenceFiles.AtomicBytes(Path.Combine(path,row.ImageFile),black);
                return row with{ImageSha256=EvidenceFiles.Hash(black),DisclosureStatus="blocked",RequirementsMet=false,ReasonCode="window-set-changed"};
            }).ToArray();
            result=result with{Windows=rows,Quality=GroupEvidenceValidation.Evaluate(work.GroupPlan,rows,false)};
            EvidenceFiles.AtomicJson(Path.Combine(path,"group.json"),result);EvidenceFiles.Save(work,EvidenceFiles.OpaquePng(new System.Drawing.Size(16,16)),new EvidenceUi(Array.Empty<EvidenceNode>(),false),DateTimeOffset.UtcNow);
        }
        return result;
    }
    GroupEvidenceManifest FinalizeFreshness(EvidenceStep step,string phase,EvidenceRequest request,GroupEvidenceManifest result)
    {
        // Current acknowledgements are checked after step.json is durable, not just when a frame
        // arrives. Historical InspectSession deliberately does not apply today's QPC age.
        long now=WgcCaptureBackend.Clock100ns();
        var expired=result.Windows.Where(row=>row.CaptureStatus=="fresh" &&
            policy.Approvals.Any(a=>a.Required && a.Hwnd==row.Window.Hwnd && a.WindowId==row.Window.WindowId && a.Generation==row.Window.Generation) &&
            (row.FrameTime100ns==null || now-row.FrameTime100ns.Value<0 || now-row.FrameTime100ns.Value>policy.MaxFrameAgeMs*10000L))
            .Select(row=>row.Window.WindowId).ToHashSet();
        if(expired.Count==0)return result;
        string path=Path.GetDirectoryName(CommitPath(step,phase));
        var plan=JsonSerializer.Deserialize<GroupCapturePlan>(ReadMetadata(Path.Combine(path,"plan.json")),EvidenceProtocol.Json);
        var rows=result.Windows.Select(row=>
        {
            bool stale=expired.Contains(row.Window.WindowId);
            if(row.ImageFile==null)return stale?row with{CaptureStatus="stale-frame",RequirementsMet=false}:row;
            var black=EvidenceFiles.OpaquePng(new System.Drawing.Size(row.Width,row.Height));
            EvidenceFiles.AtomicBytes(Path.Combine(path,row.ImageFile),black);
            return row with{ImageSha256=EvidenceFiles.Hash(black),CaptureStatus=stale?"stale-frame":row.CaptureStatus,
                DisclosureStatus="blocked",RequirementsMet=false,ReasonCode="state-unproven"};
        }).ToArray();
        var quality=GroupEvidenceValidation.Evaluate(plan,rows,true);
        quality=quality with{RequirementsMet=false,DiagnosticStatus="insufficient",Reasons=quality.Reasons.Concat(result.Quality.Reasons).Distinct(StringComparer.Ordinal).ToArray()};
        var updated=result with{Windows=rows,Quality=quality};
        EvidenceFiles.AtomicJson(Path.Combine(path,"group.json"),updated);
        var work=new CaptureWork(id,step.StepId,step.Token,phase,target,new(),path,started,request.DeadlineUtc,GroupPlan:plan);
        EvidenceFiles.Save(work,EvidenceFiles.OpaquePng(new System.Drawing.Size(16,16)),new EvidenceUi(Array.Empty<EvidenceNode>(),false),DateTimeOffset.UtcNow);
        EvidenceFiles.Verify(work);GroupEvidenceValidation.Verify(work,updated);
        return updated;
    }
    EvidenceReply Inspect(EvidenceRequest request)
    {
        string root=request.OutputDirectory;EvidenceFiles.RequireLocalUnlinkedPath(root);
        using var json=JsonDocument.Parse(ReadMetadata(Path.Combine(root,"external-session.json")));
        if(json.RootElement.GetProperty("product").GetString()!="operation-recorder-evidence-group"||json.RootElement.GetProperty("schemaVersion").GetInt32()!=2||json.RootElement.GetProperty("sessionId").GetGuid()!=request.SessionId)throw new EvidenceUnavailableException("session-mismatch");
        var list=new List<EvidenceStepSummary>();string folder=Path.Combine(root,"steps");
        if(Directory.Exists(folder))
        {
            EvidenceFiles.RequireLocalUnlinkedPath(folder);
            var directories=Directory.EnumerateDirectories(folder).Take(10001).ToArray();
            if(directories.Length>10000)throw new EvidenceUnavailableException("session-limit");
            foreach(var dir in directories)
            {
            if(request.StepId!=Guid.Empty && (!Guid.TryParseExact(Path.GetFileName(dir),"N",out var selectedId)||selectedId!=request.StepId))continue;
            EvidenceFiles.RequireLocalUnlinkedPath(dir);var step=JsonSerializer.Deserialize<EvidenceStep>(ReadMetadata(Path.Combine(dir,"step.json")),EvidenceProtocol.Json);
            if(!Guid.TryParseExact(Path.GetFileName(dir),"N",out var directoryId)||directoryId!=step.StepId)throw new EvidenceUnavailableException("save-unconfirmed");
            if(request.StepId!=Guid.Empty&&request.StepId!=step.StepId)continue;
            bool Has(string phase)
            {
                try
                {
                    string path=Path.Combine(dir,phase);EvidenceFiles.RequireLocalUnlinkedPath(path);
                    var planFile=Path.Combine(path,"plan.json");EvidenceFiles.RequireLocalUnlinkedPath(planFile);
                    var plan=JsonSerializer.Deserialize<GroupCapturePlan>(ReadMetadata(planFile),EvidenceProtocol.Json);
                    if(plan.AppSha256!=json.RootElement.GetProperty("appHash").GetString())return false;
                    var work=new CaptureWork(request.SessionId,step.StepId,step.Token,phase,json.RootElement.GetProperty("target").Deserialize<EvidenceTarget>(EvidenceProtocol.Json),new(),path,json.RootElement.GetProperty("started").GetDateTimeOffset(),request.DeadlineUtc,GroupPlan:plan);
                    EvidenceFiles.Verify(work);
                    GroupEvidenceValidation.Verify(work,JsonSerializer.Deserialize<GroupEvidenceManifest>(ReadMetadata(Path.Combine(path,"group.json")),EvidenceProtocol.Json));return true;
                }
                catch{return false;}
            }
            bool before=Has("before"),after=Has("after");
            list.Add(new(step.StepId,step.Token,step.Status=="completed"&&step.Outcome!="unknown"&&before&&after?"completed":"indeterminate",before,after,Has("failure"),step.Outcome is "success" or "failure" or "unknown"?step.Outcome:"unknown"));
            }
        }
        return new(){SchemaVersion=2,RequestId=request.RequestId,SessionId=request.SessionId,Status="inspected",StepStates=list.Take(100).ToArray(),HasMoreSteps=list.Count>100,PendingSteps=list.Where(s=>s.Status!="completed").Select(s=>s.StepId).Take(100).ToArray()};
    }
    public void Dispose(){fileLock?.Dispose();registry?.Dispose();gate.Dispose();}
    static bool ValidState(EvidenceState value)=>value==null||((value.ControlTypeId==null||value.ControlTypeId is >=50000 and <=50100)&&(value.FingerprintSha256==null||EvidenceProtocol.IsHash(value.FingerprintSha256)));
    static byte[] ReadMetadata(string path)
    {
        EvidenceFiles.RequireLocalUnlinkedPath(path);if(new FileInfo(path).Length>262144)throw new EvidenceUnavailableException("message-limit");
        var bytes=File.ReadAllBytes(path);if(bytes.Length>262144)throw new EvidenceUnavailableException("message-limit");return bytes;
    }
}
