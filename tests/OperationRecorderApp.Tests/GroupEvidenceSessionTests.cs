using System;
using System.Drawing;
using System.IO;
using System.Linq;
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
        public Task<string> CaptureAndSaveAsync(CaptureWork work,CancellationToken token)
        {
            Calls++;var plan=work.GroupPlan;var window=plan.Snapshot.Windows[0];var grant=plan.Policy.Approvals[0];
            string image="windows/"+window.WindowId.ToString("N")+"/frame.png",ui="windows/"+window.WindowId.ToString("N")+"/ui.json";
            using var bitmap=new Bitmap(24,24);using(var graphics=Graphics.FromImage(bitmap))graphics.Clear(Good?Color.Blue:Color.Red);
            var decision=DisclosureGate.Apply(bitmap,plan.AppSha256,grant.Role,96,plan.Policy.Disclosure,Array.Empty<Rectangle>(),true);
            var png=decision.Png;EvidenceFiles.AtomicBytes(Path.Combine(work.Directory,image),png);EvidenceFiles.AtomicJson(Path.Combine(work.Directory,ui),new EvidenceUi(Array.Empty<EvidenceNode>(),false));
            var row=new WindowEvidence(window,grant.Role,"wgc-window",null,WgcCaptureBackend.Clock100ns()+FrameShift100ns,24,24,image,EvidenceFiles.Hash(png),ui,EvidenceFiles.HashFile(Path.Combine(work.Directory,ui)),"fresh",decision.DisclosureStatus,decision.RequirementsMet,decision.ReasonCode,decision.VerifiedFrameSha256);
            var rows=Empty?Array.Empty<WindowEvidence>():new[]{row};var quality=GroupEvidenceValidation.Evaluate(plan,rows,true);
            if(Empty)quality=quality with{RequirementsMet=true};
            var result=new GroupEvidenceManifest(2,work.SessionId,work.StepId,work.StepToken,work.Phase,plan.Snapshot.Revision,EvidenceFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(plan.Policy,EvidenceProtocol.Json)),DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,rows,quality);
            EvidenceFiles.AtomicJson(Path.Combine(work.Directory,"group.json"),result);string path=EvidenceFiles.Save(work,png,new EvidenceUi(Array.Empty<EvidenceNode>(),false),DateTimeOffset.UtcNow);After?.Invoke();return Task.FromResult(path);
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
}
