using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

public sealed class GroupEvidenceSessionTests
{
    sealed class Backend : IEvidenceBackend
    {
        public int Calls;public bool Good=true;public bool Empty;public Action After;public long FrameShift100ns;
        public Func<CaptureWork,CancellationToken,Task> BeforeCapture;
        public async Task<string> CaptureAndSaveAsync(CaptureWork work,CancellationToken token)
        {
            Calls++;
            if(BeforeCapture!=null)await BeforeCapture(work,token);
            var plan=work.GroupPlan;var window=plan.Snapshot.Windows[0];var grant=plan.Policy.Approvals[0];
            string image="windows/"+window.WindowId.ToString("N")+"/frame.png",ui="windows/"+window.WindowId.ToString("N")+"/ui.json";
            using var bitmap=new Bitmap(24,24);using(var graphics=Graphics.FromImage(bitmap))graphics.Clear(Good?Color.Blue:Color.Red);
            var decision=DisclosureGate.Apply(bitmap,plan.AppSha256,grant.Role,96,plan.Policy.Disclosure,Array.Empty<Rectangle>(),true);
            var png=decision.Png;EvidenceFiles.AtomicBytes(Path.Combine(work.Directory,image),png);EvidenceFiles.AtomicJson(Path.Combine(work.Directory,ui),new EvidenceUi(Array.Empty<EvidenceNode>(),false));
            var row=new WindowEvidence(window,grant.Role,"wgc-window",null,WgcCaptureBackend.Clock100ns()+FrameShift100ns,24,24,image,EvidenceFiles.Hash(png),ui,EvidenceFiles.HashFile(Path.Combine(work.Directory,ui)),"fresh",decision.DisclosureStatus,decision.RequirementsMet,decision.ReasonCode,decision.VerifiedFrameSha256);
            var rows=Empty?Array.Empty<WindowEvidence>():new[]{row};var quality=GroupEvidenceValidation.Evaluate(plan,rows,true);
            if(Empty)quality=quality with{RequirementsMet=true};
            var result=new GroupEvidenceManifest(2,work.SessionId,work.StepId,work.StepToken,work.Phase,plan.Snapshot.Revision,EvidenceFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(plan.Policy,EvidenceProtocol.Json)),DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,rows,quality);
            EvidenceFiles.AtomicJson(Path.Combine(work.Directory,"group.json"),result);string path=EvidenceFiles.Save(work,png,new EvidenceUi(Array.Empty<EvidenceNode>(),false),DateTimeOffset.UtcNow);After?.Invoke();return path;
        }
    }
    sealed class Context:IDisposable
    {
        public readonly string Root=Path.Combine(Path.GetTempPath(),"oprec-group-"+Guid.NewGuid().ToString("N"));
        public readonly Guid Id=Guid.NewGuid();public readonly Backend Backend=new();public readonly GroupEvidenceSession Session;
        public WindowGraphSnapshot Graph;
        public Context()
        {
            Directory.CreateDirectory(Root);Graph=new(1,false,new[]{new WindowObservation(Guid.NewGuid(),1,10,0,0,123,false,true,false,false,new Rectangle(0,0,24,24),96,"root")});
            Session=new(Backend,(_,_)=>Graph);
        }
        public EvidenceRequest Request(string method,Guid step=default,Guid token=default)=>new(){SchemaVersion=2,RequestId=Guid.NewGuid(),SessionId=Id,Method=method,StepId=step,StepToken=token,DeadlineUtc=DateTimeOffset.UtcNow.AddSeconds(5),OperationName="invoke",Outcome="success"};
        public string SessionDirectory => Path.Combine(Root,"session");
        public EvidenceRequest SnapshotRequest() => Request("CaptureSnapshot") with { OperationName=null, Outcome=null };
        public Task<EvidenceReply> Start()
        {
            using var bitmap=new Bitmap(24,24);using(var graphics=Graphics.FromImage(bitmap))graphics.Clear(Color.Blue);
            string hash=DisclosureGate.PixelHash(bitmap,new Rectangle(0,0,24,24));
            var profile=new DisclosureProfile(1,"fixture-public",1,EvidenceFiles.HashFile(typeof(EvidenceSession).Assembly.Location),new[]{new PublicRegionRule("blue","root",96,24,24,new Rectangle(0,0,24,24),new[]{hash})},new[]{hash});
            return Session.HandleAsync(Request("StartSession")with{Target=new(123,DateTimeOffset.UtcNow,typeof(EvidenceSession).Assembly.Location,1,10),OutputDirectory=Path.Combine(Root,"session"),GroupPolicy=new(Disclosure:profile)});
        }
        public void Dispose(){Session.Dispose();Directory.Delete(Root,true);}
    }
    [Fact]public async Task schema2保存品質と重複と前後を対応付ける()
    {
        using var c=new Context();Assert.Equal("started",(await c.Start()).Status);
        var request=c.Request("BeginStep",Guid.NewGuid());var before=await c.Session.HandleAsync(request);
        Assert.True(before.OperationAllowed,before.Status);Assert.Equal(2,before.SchemaVersion);Assert.True(before.Quality.RequirementsMet);
        Assert.False((await c.Session.HandleAsync(request)).OperationAllowed);
        var after=await c.Session.HandleAsync(c.Request("EndStep",token:before.StepToken));Assert.Equal("after-saved",after.Status);Assert.Empty(after.PendingSteps);
        Assert.Equal(2,c.Backend.Calls);Assert.Equal("stopped",(await c.Session.HandleAsync(c.Request("StopSession"))).Status);
    }
    [Theory]
    [InlineData(true, "deadline-exceeded")]
    [InlineData(false, "caller-unavailable")]
    public async Task Expired_or_cancelled_transition_ack_cannot_be_retried(bool expired, string expectedStatus)
    {
        using var context = new Context();
        var started = await context.Start();
        Assert.DoesNotContain("approved-window-transition", started.Capabilities);
        var begin = await context.Session.HandleAsync(context.Request("BeginStep", Guid.NewGuid()));
        Assert.True(begin.OperationAllowed);

        // Register an issued transition token without a live Win32 fixture. The expiration
        // and retry paths run before native observation or any capture backend call.
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var steps = (Dictionary<Guid, EvidenceStep>)typeof(GroupEvidenceSession).GetField("steps", fields).GetValue(context.Session);
        var step = steps[begin.StepId];
        step.ExpectWindowTransition = true;
        typeof(GroupEvidenceSession).GetMethod("RegisterTransitionAttempt", fields).Invoke(context.Session, new[] { step });
        var end = context.Request("EndStep", token: begin.StepToken);

        var rejected = await context.Session.HandleAsync(end with
        {
            Target = new EvidenceTarget(222, DateTimeOffset.UtcNow, @"C:\fixture.exe", 1, 56)
        });
        Assert.Equal("invalid-request", rejected.Status);
        var missed = expired
            ? await context.Session.HandleAsync(end with { DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(-1) })
            : await context.Session.HandleAsync(end, new CancellationToken(true));
        Assert.Equal(expectedStatus, missed.Status);

        var retry = await context.Session.HandleAsync(context.Request("EndStep", token: begin.StepToken));
        Assert.Equal("step-indeterminate", retry.Status);
        Assert.Equal(begin.StepId, Assert.Single(retry.PendingSteps));
        Assert.Equal(1, context.Backend.Calls);
        var saved = JsonSerializer.Deserialize<EvidenceStep>(
            File.ReadAllBytes(Path.Combine(context.SessionDirectory, "steps", begin.StepId.ToString("N"), "step.json")), EvidenceProtocol.Json);
        Assert.Equal("after-unconfirmed", saved.Status);
        Assert.Equal("unknown", saved.Outcome);
    }
    [Fact]public async Task 黒画像の保存だけでは診断成功や操作許可を返さない()
    {
        using var c=new Context();await c.Start();c.Backend.Good=false;
        int actions=0;var result=await c.Session.HandleAsync(c.Request("BeginStep",Guid.NewGuid()));if(result.OperationAllowed)actions++;
        Assert.Equal(0,actions);Assert.Equal("before-inadequate",result.Status);Assert.Equal("insufficient",result.Quality.DiagnosticStatus);Assert.Single(result.EvidencePaths);
    }
    [Fact]public async Task HWND同値の世代変更後は再保護して保存状態も不足にする()
    {
        using var c=new Context();await c.Start();c.Backend.After=()=>c.Graph=c.Graph with{Windows=c.Graph.Windows.Select(w=>w with{WindowId=Guid.NewGuid(),Generation=2}).ToArray()};
        var result=await c.Session.HandleAsync(c.Request("BeginStep",Guid.NewGuid()));Assert.False(result.OperationAllowed);Assert.False(result.Quality.RequirementsMet);
        var group=JsonSerializer.Deserialize<GroupEvidenceManifest>(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(result.EvidencePaths[0]),"group.json")),EvidenceProtocol.Json);
        Assert.False(group.Quality.RequirementsMet);Assert.All(group.Windows,w=>Assert.Equal("blocked",w.DisclosureStatus));
    }
    [Fact]public async Task 空のwindow集合を成功と偽装した保存を拒否する()
    {
        using var c=new Context();await c.Start();c.Backend.Empty=true;
        var result=await c.Session.HandleAsync(c.Request("BeginStep",Guid.NewGuid()));Assert.False(result.OperationAllowed);Assert.Equal("save-unconfirmed",result.Status);
    }
    [Fact]public async Task 既存承認の降格を同じセッションで受け付けない()
    {
        using var c=new Context();await c.Start();var window=c.Graph.Windows[0];
        var result=await c.Session.HandleAsync(c.Request("ApproveWindows")with{WindowApprovals=new[]{new WindowApproval(10,"root",false,window.WindowId,window.Generation)}});
        Assert.Equal("approval-change-requires-new-session",result.Status);
    }
    [Fact]public async Task Inspectは破損した画像を保存済みとしない()
    {
        using var c=new Context();await c.Start();var result=await c.Session.HandleAsync(c.Request("BeginStep",Guid.NewGuid()));
        string phase=Path.GetDirectoryName(result.EvidencePaths[0]);File.AppendAllText(Path.Combine(phase,"frame.png"),"tamper");
        using var inspector=new GroupEvidenceSession(c.Backend,(_,_)=>c.Graph);
        var inspection=await inspector.HandleAsync(c.Request("InspectSession")with{OutputDirectory=Path.Combine(c.Root,"session")});
        Assert.Equal("inspected",inspection.Status);Assert.False(Assert.Single(inspection.StepStates).BeforeSaved);
    }
    [Fact]public void schema1のJSONへ新しい拡張項目を勝手に追加しない()
    {
        string json=JsonSerializer.Serialize(new EvidenceReply(),EvidenceProtocol.Json);Assert.DoesNotContain("quality",json);Assert.DoesNotContain("windowGraph",json);
        string request=JsonSerializer.Serialize(new EvidenceRequest(),EvidenceProtocol.Json);Assert.DoesNotContain("groupPolicy",request);
    }
    [Theory]
    [InlineData("before",-1)]
    [InlineData("before",1)]
    [InlineData("after",-1)]
    [InlineData("failure",-1)]
    public async Task ACK時に期限外のフレームは再保護して過去保存検査とは区別する(string phase,int direction)
    {
        using var c=new Context();await c.Start();Guid step=Guid.NewGuid();EvidenceReply before=null;
        if(phase!="before")before=await c.Session.HandleAsync(c.Request("BeginStep",step));
        c.Backend.FrameShift100ns=direction*TimeSpan.FromSeconds(4).Ticks;
        var request=phase switch{
            "before"=>c.Request("BeginStep",step),
            "after"=>c.Request("EndStep",token:before.StepToken),
            _=>c.Request("RecordFailure",step) with{ErrorCode="unexpected-state"}};
        int operations=0;var response=await c.Session.HandleAsync(request);if(response.OperationAllowed)operations++;
        Assert.Equal(0,operations);Assert.False(response.Quality.RequirementsMet);Assert.Equal("insufficient",response.Quality.DiagnosticStatus);
        Assert.Equal(phase=="failure"?"failure-saved":phase+"-inadequate",response.Status);
        string path=Path.GetDirectoryName(Assert.Single(response.EvidencePaths));
        var group=JsonSerializer.Deserialize<GroupEvidenceManifest>(File.ReadAllBytes(Path.Combine(path,"group.json")),EvidenceProtocol.Json);
        Assert.False(group.Quality.RequirementsMet);Assert.All(group.Windows,row=>{Assert.Equal("stale-frame",row.CaptureStatus);Assert.Equal("blocked",row.DisclosureStatus);});
        foreach(string file in Directory.GetFiles(path,"*.png",SearchOption.AllDirectories))
        {
            using var bitmap=new Bitmap(file);
            for(int y=0;y<bitmap.Height;y++)for(int x=0;x<bitmap.Width;x++)Assert.Equal(Color.Black.ToArgb(),bitmap.GetPixel(x,y).ToArgb());
        }
        using var inspector=new GroupEvidenceSession(c.Backend,(_,_)=>c.Graph);
        var inspected=await inspector.HandleAsync(c.Request("InspectSession") with{OutputDirectory=Path.Combine(c.Root,"session"),StepId=step});
        Assert.Equal("inspected",inspected.Status);var state=Assert.Single(inspected.StepStates);
        Assert.True(phase switch{"before"=>state.BeforeSaved,"after"=>state.AfterSaved,_=>state.FailureSaved});
        Assert.Equal("indeterminate",state.Status);
    }
    [Fact]public async Task Inspectは百件を超えた未完了と指定工程を隠さない()
    {
        using var c=new Context();await c.Start();string root=Path.Combine(c.Root,"session"),steps=Path.Combine(root,"steps");
        for(int index=0;index<101;index++)
        {
            var step=new EvidenceStep{StepId=Guid.NewGuid(),Token=Guid.NewGuid(),Sequence=index+1,Status="before-unconfirmed"};
            EvidenceFiles.AtomicJson(Path.Combine(steps,step.StepId.ToString("N"),"step.json"),step);
        }
        var last=Guid.ParseExact(Path.GetFileName(Directory.EnumerateDirectories(steps).Last()),"N");
        using var inspector=new GroupEvidenceSession(c.Backend,(_,_)=>c.Graph);
        var all=await inspector.HandleAsync(c.Request("InspectSession") with{OutputDirectory=root});
        Assert.Equal("inspected",all.Status);Assert.True(all.HasMoreSteps);Assert.Equal(100,all.StepStates.Length);Assert.Equal(100,all.PendingSteps.Length);
        var selected=await inspector.HandleAsync(c.Request("InspectSession") with{OutputDirectory=root,StepId=last});
        Assert.Equal("inspected",selected.Status);Assert.False(selected.HasMoreSteps);Assert.Equal(last,Assert.Single(selected.StepStates).StepId);
        Assert.Equal(last,Assert.Single(selected.PendingSteps));
    }

    [Fact]
    public async Task 単発取得は専用の場所に確定して操作工程を作らない()
    {
        using var context=new Context();
        Assert.Equal("started",(await context.Start()).Status);
        var request=context.SnapshotRequest();
        var reply=await context.Session.HandleAsync(request);

        Assert.Equal("snapshot-saved",reply.Status);
        AssertSnapshotReply(reply,request);
        Assert.True(reply.Quality.RequirementsMet);
        Assert.Empty(reply.PendingSteps);
        Assert.False(Directory.Exists(Path.Combine(context.SessionDirectory,"steps")));
        string commitPath=Assert.Single(reply.EvidencePaths);
        string output=Path.Combine(context.SessionDirectory,"snapshots",request.RequestId.ToString("N"));
        Assert.Equal(Path.Combine(output,"commit.json"),commitPath);
        var commit=JsonSerializer.Deserialize<EvidenceCommit>(File.ReadAllBytes(commitPath),EvidenceProtocol.Json);
        Assert.Equal("snapshot",commit.Phase);
        Assert.Equal(context.Id,commit.SessionId);
        string groupPath=Path.Combine(output,"group.json");
        var group=JsonSerializer.Deserialize<GroupEvidenceManifest>(File.ReadAllBytes(groupPath),EvidenceProtocol.Json);
        Assert.Equal(group.CollectionEnded,reply.CollectionEndedUtc);
        Assert.Equal(EvidenceFiles.HashFile(groupPath),commit.GroupSha256);
        Assert.Equal(EvidenceFiles.HashFile(Path.Combine(output,"frame.png")),commit.ImageSha256);
        using(var image=new Bitmap(Path.Combine(output,"frame.png")))
            Assert.Equal(Color.Blue.ToArgb(),image.GetPixel(10,10).ToArgb());

        var duplicate=await context.Session.HandleAsync(request);
        Assert.Equal("duplicate-request",duplicate.Status);
        Assert.False(duplicate.OperationAllowed);
        Assert.Empty(duplicate.EvidencePaths);
        Assert.Equal(1,context.Backend.Calls);
        Assert.Equal(commit.ImageSha256,EvidenceFiles.HashFile(Path.Combine(output,"frame.png")));

        var inspect=await context.Session.HandleAsync(context.Request("InspectSession") with { OutputDirectory=context.SessionDirectory });
        Assert.Equal("inspected",inspect.Status);
        Assert.Empty(inspect.StepStates);
        Assert.Empty(inspect.PendingSteps);
    }

    [Fact]
    public async Task 単発取得の要求で対象と保存先と工程を差し替えられない()
    {
        using var context=new Context();
        Assert.Equal("started",(await context.Start()).Status);
        var target=new EvidenceTarget(456,DateTimeOffset.UtcNow,typeof(EvidenceSession).Assembly.Location,1,20);
        string otherDirectory=Path.Combine(context.Root,"unrelated");
        var invalidRequests=new[]
        {
            context.SnapshotRequest() with { Target=target },
            context.SnapshotRequest() with { OutputDirectory=otherDirectory },
            context.SnapshotRequest() with { StepId=Guid.NewGuid() },
            context.SnapshotRequest() with { OperationName="invoke" }
        };
        foreach(var request in invalidRequests)
        {
            var reply=await context.Session.HandleAsync(request);
            Assert.Equal("invalid-request",reply.Status);
            Assert.False(reply.OperationAllowed);
            Assert.Empty(reply.EvidencePaths);
        }
        Assert.Equal(0,context.Backend.Calls);
        Assert.False(Directory.Exists(otherDirectory));
        Assert.False(Directory.Exists(Path.Combine(context.SessionDirectory,"snapshots")));
        Assert.False(Directory.Exists(Path.Combine(context.SessionDirectory,"steps")));
    }

    [Fact]
    public async Task 単発取得は保留中の前後工程と操作許可を変更しない()
    {
        using var context=new Context();
        Assert.Equal("started",(await context.Start()).Status);
        var stepId=Guid.NewGuid();
        var before=await context.Session.HandleAsync(context.Request("BeginStep",stepId));
        Assert.True(before.OperationAllowed,before.Status);

        var request=context.SnapshotRequest();
        var reply=await context.Session.HandleAsync(request);
        Assert.Equal("snapshot-saved",reply.Status);
        AssertSnapshotReply(reply,request);
        Assert.Equal(stepId,Assert.Single(reply.PendingSteps));
        var next=await context.Session.HandleAsync(context.Request("BeginStep",Guid.NewGuid()));
        Assert.Equal("pending-step",next.Status);
        Assert.False(next.OperationAllowed);

        var after=await context.Session.HandleAsync(context.Request("EndStep",token:before.StepToken));
        Assert.Equal("after-saved",after.Status);
        Assert.Empty(after.PendingSteps);
        var inspect=await context.Session.HandleAsync(context.Request("InspectSession") with { OutputDirectory=context.SessionDirectory });
        var savedStep=Assert.Single(inspect.StepStates);
        Assert.Equal(stepId,savedStep.StepId);
        Assert.Equal("completed",savedStep.Status);
        Assert.True(savedStep.BeforeSaved);
        Assert.True(savedStep.AfterSaved);
        Assert.Equal(3,context.Backend.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 公開不一致や応答時の鮮度不足でも単発取得は黒画素だけを確定する(bool stale)
    {
        using var context=new Context();
        Assert.Equal("started",(await context.Start()).Status);
        context.Backend.Good=stale;
        if(stale)context.Backend.FrameShift100ns=-TimeSpan.FromSeconds(4).Ticks;
        var request=context.SnapshotRequest();
        var reply=await context.Session.HandleAsync(request);

        Assert.Equal("snapshot-inadequate",reply.Status);
        AssertSnapshotReply(reply,request);
        Assert.False(reply.Quality.RequirementsMet);
        Assert.Equal("insufficient",reply.Quality.DiagnosticStatus);
        Assert.Empty(reply.PendingSteps);
        string output=Path.GetDirectoryName(Assert.Single(reply.EvidencePaths));
        var group=JsonSerializer.Deserialize<GroupEvidenceManifest>(File.ReadAllBytes(Path.Combine(output,"group.json")),EvidenceProtocol.Json);
        var row=Assert.Single(group.Windows);
        Assert.False(group.Quality.RequirementsMet);
        Assert.Equal(stale?"stale-frame":"fresh",row.CaptureStatus);
        foreach(string leaf in new[]{"frame.png",row.ImageFile})
        {
            string path=Path.Combine(output,leaf);
            using var bitmap=new Bitmap(path);
            for(int y=0;y<bitmap.Height;y++)
                for(int x=0;x<bitmap.Width;x++)
                    Assert.Equal(Color.Black.ToArgb(),bitmap.GetPixel(x,y).ToArgb());
        }
        Assert.Equal(row.ImageSha256,EvidenceFiles.HashFile(Path.Combine(output,row.ImageFile)));
    }

    [Fact]
    public async Task 別セッションの単発取得が使用中なら即時拒否して開放後に新規要求を処理する()
    {
        using var first=new Context();
        using var second=new Context();
        Assert.Equal("started",(await first.Start()).Status);
        Assert.Equal("started",(await second.Start()).Status);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Backend.BeforeCapture=async (_,token)=>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        };
        var firstRequest=first.SnapshotRequest() with { DeadlineUtc=DateTimeOffset.UtcNow.AddSeconds(15) };
        var active=first.Session.HandleAsync(firstRequest);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            var secondRequest=second.SnapshotRequest();
            var busy=await second.Session.HandleAsync(secondRequest).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("snapshot-busy",busy.Status);
            Assert.False(busy.OperationAllowed);
            Assert.Empty(busy.EvidencePaths);
            Assert.Equal(0,second.Backend.Calls);
            Assert.False(Directory.Exists(Path.Combine(second.SessionDirectory,"snapshots")));

            // The single-snapshot lease must not block the established before/after flow.
            var before=await second.Session.HandleAsync(second.Request("BeginStep",Guid.NewGuid()));
            Assert.True(before.OperationAllowed,before.Status);
            var after=await second.Session.HandleAsync(second.Request("EndStep",token:before.StepToken));
            Assert.Equal("after-saved",after.Status);
        }
        finally
        {
            release.TrySetResult();
            await active;
        }
        Assert.Equal("snapshot-saved",(await active).Status);
        var next=await second.Session.HandleAsync(second.SnapshotRequest());
        Assert.Equal("snapshot-saved",next.Status);
        Assert.Equal(3,second.Backend.Calls);
    }

    [Fact]
    public async Task 単発取得で期限を超えると保存成功を返さず次の要求は取得できる()
    {
        using var context=new Context();
        Assert.Equal("started",(await context.Start()).Status);
        context.Backend.BeforeCapture=(_,token)=>Task.Delay(Timeout.InfiniteTimeSpan,token);
        var request=context.SnapshotRequest() with { DeadlineUtc=DateTimeOffset.UtcNow.AddSeconds(1) };
        var expired=await context.Session.HandleAsync(request).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("deadline-exceeded",expired.Status);
        Assert.False(expired.OperationAllowed);
        Assert.Null(expired.SavedAt);
        Assert.Empty(expired.EvidencePaths);
        Assert.False(File.Exists(Path.Combine(context.SessionDirectory,"snapshots",request.RequestId.ToString("N"),"commit.json")));

        context.Backend.BeforeCapture=null;
        Assert.Equal("snapshot-saved",(await context.Session.HandleAsync(context.SnapshotRequest())).Status);
    }

    static void AssertSnapshotReply(EvidenceReply reply,EvidenceRequest request)
    {
        Assert.Equal(2,reply.SchemaVersion);
        Assert.Equal(request.RequestId,reply.RequestId);
        Assert.Equal(request.SessionId,reply.SessionId);
        Assert.Equal(Guid.Empty,reply.StepId);
        Assert.Equal(Guid.Empty,reply.StepToken);
        Assert.False(reply.OperationAllowed);
        Assert.NotNull(reply.SavedAt);
        Assert.NotNull(reply.CollectionEndedUtc);
        Assert.NotNull(reply.Quality);
    }
}
