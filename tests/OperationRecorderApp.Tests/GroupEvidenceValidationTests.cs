using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

public sealed class GroupEvidenceValidationTests
{
    sealed class Context:IDisposable
    {
        public string Root=Path.Combine(Path.GetTempPath(),"oprec-group-validation-"+Guid.NewGuid().ToString("N"));
        public WindowObservation Window=new(Guid.NewGuid(),1,11,0,0,77,false,true,false,false,new Rectangle(0,0,2,2),96,"root");
        public GroupCapturePlan Plan;
        public CaptureWork Work;
        public WindowEvidence Row;
        public Context(bool publicPixels=false)
        {
            Plan=new(new(5,false,new[]{Window}),new(new[]{new WindowApproval(11,"root",true,Window.WindowId,1)}),new string('a',64));
            Work=new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"before",new(77,DateTimeOffset.UtcNow,"C:/fixture.exe",1,11),new(),Root,
                DateTimeOffset.UtcNow.AddHours(-1),DateTimeOffset.UtcNow.AddSeconds(10),GroupPlan:Plan);
            Write(publicPixels);
        }
        public void Write(bool publish,Rectangle? region=null)
        {
            byte[] png;string disclosure="opaque-only",reason="profile-required",frameHash=null;bool met=false;
            if(publish)
            {
                using var bitmap=new Bitmap(2,2);using(var graphics=Graphics.FromImage(bitmap))graphics.Clear(Color.Blue);
                var rectangle=region??new Rectangle(0,0,2,2);
                frameHash=DisclosureGate.PixelHash(bitmap,new Rectangle(0,0,2,2));
                var profile=new DisclosureProfile(1,"fixture",1,Plan.AppSha256,new[]{new PublicRegionRule("public","root",96,2,2,rectangle,new[]{DisclosureGate.PixelHash(bitmap,rectangle)})},new[]{frameHash});
                Plan=Plan with {Policy=Plan.Policy with {Disclosure=profile}};Work=Work with{GroupPlan=Plan};
                var decision=DisclosureGate.Apply(bitmap,Plan.AppSha256,"root",96,profile,Array.Empty<Rectangle>(),true);
                png=decision.Png;disclosure=decision.DisclosureStatus;reason=decision.ReasonCode;met=decision.RequirementsMet;
            }
            else png=EvidenceFiles.OpaquePng(new Size(2,2));
            string prefix="windows/"+Window.WindowId.ToString("N")+"/";
            EvidenceFiles.AtomicBytes(Path.Combine(Root,prefix+"frame.png"),png);
            EvidenceFiles.AtomicJson(Path.Combine(Root,prefix+"ui.json"),new EvidenceUi(Array.Empty<EvidenceNode>(),false));
            Row=new(Window,"root","wgc-window",null,1000,2,2,prefix+"frame.png",EvidenceFiles.Hash(png),prefix+"ui.json",EvidenceFiles.HashFile(Path.Combine(Root,prefix+"ui.json")),"fresh",disclosure,met,reason,frameHash);
        }
        public GroupEvidenceManifest Manifest(WindowEvidence[] rows=null,GroupQuality quality=null)
        {
            rows??=new[]{Row};
            return new(2,Work.SessionId,Work.StepId,Work.StepToken,"before",Plan.Snapshot.Revision,
                EvidenceFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(Plan.Policy,EvidenceProtocol.Json)),Work.SessionStartedUtc.AddSeconds(1),Work.SessionStartedUtc.AddSeconds(2),rows,
                quality??GroupEvidenceValidation.Evaluate(Plan,rows,true));
        }
        public void Reject(GroupEvidenceManifest manifest)
        { var error=Assert.Throws<EvidenceUnavailableException>(()=>GroupEvidenceValidation.Verify(Work,manifest));Assert.Equal("save-unconfirmed",error.Code); }
        public void Dispose()
        {
            var windows=Path.Combine(Root,"windows");
            if(Directory.Exists(windows)&&(File.GetAttributes(windows)&FileAttributes.ReparsePoint)!=0)Directory.Delete(windows);
            if(Directory.Exists(Root))Directory.Delete(Root,true);
        }
    }
    [Fact]
    public void 保存時の古い時刻でも正常な成果物を検証し全黒を診断成功としない()
    {
        using var c=new Context();c.Plan=c.Plan with{Policy=c.Plan.Policy with{RequirePublicDiagnostic=false}};c.Work=c.Work with{GroupPlan=c.Plan};
        var manifest=c.Manifest();Assert.True(manifest.Quality.RequirementsMet);Assert.Equal("insufficient",manifest.Quality.DiagnosticStatus);
        GroupEvidenceValidation.Verify(c.Work,manifest);
        GroupEvidenceValidation.Verify(c.Work with{SessionStartedUtc=DateTimeOffset.UtcNow},manifest);
        c.Reject(manifest with{Quality=manifest.Quality with{DiagnosticStatus="sufficient"}});
    }
    [Fact]
    public void 本Gateで承認された公開画素だけが正常検証を通る()
    {
        using var c=new Context(true);var manifest=c.Manifest();Assert.True(manifest.Quality.RequirementsMet);Assert.Equal("sufficient",manifest.Quality.DiagnosticStatus);
        GroupEvidenceValidation.Verify(c.Work,manifest);
        c.Reject(manifest with{Windows=new[]{c.Row with{VerifiedFrameSha256=new string('f',64)}}});
        var empty=c.Plan with{Policy=c.Plan.Policy with{Disclosure=null}};
        c.Work=c.Work with{GroupPlan=empty};
        c.Reject(manifest with{PolicySha256=EvidenceFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(empty.Policy,EvidenceProtocol.Json))});
    }
    [Fact]
    public void 空の窓一覧で保存完了や品質成功を偽装できない()
    {
        using var c=new Context();c.Reject(c.Manifest(Array.Empty<WindowEvidence>(),new("captured","public-regions","sufficient",true,Array.Empty<string>(),Array.Empty<Guid>())));
    }
    [Fact]
    public void 理由コードへ秘密や任意の文字列を混ぜない()
    {
        using var c=new Context();var manifest=c.Manifest();
        c.Reject(manifest with{Quality=manifest.Quality with{Reasons=new[]{"DUMMY_SECRET_ERROR"}}});
        c.Reject(c.Manifest(new[]{c.Row with{ReasonCode="DUMMY_SECRET_ROW"}}));
    }
    [Theory]
    [InlineData("id")][InlineData("generation")][InlineData("hwnd")][InlineData("parent")][InlineData("owner")][InlineData("pid")][InlineData("bounds")][InlineData("role")]
    public void 承認対象と異なる識別や所属や役割を保存済みと認めない(string kind)
    {
        using var c=new Context();var window=kind switch{
            "id"=>c.Window with{WindowId=Guid.NewGuid()},"generation"=>c.Window with{Generation=2},"hwnd"=>c.Window with{Hwnd=99},
            "parent"=>c.Window with{ParentHwnd=99},"owner"=>c.Window with{OwnerHwnd=99},"pid"=>c.Window with{Pid=88},"bounds"=>c.Window with{Bounds=new(1,1,2,2)},_=>c.Window};
        c.Reject(c.Manifest(new[]{c.Row with{Window=window,Role=kind=="role"?"other":c.Row.Role}}));
    }
    [Theory]
    [InlineData("../outside.png")][InlineData("C:/outside.png")][InlineData("windows/wrong/frame.png")][InlineData("windows\\wrong\\frame.png")]
    public void 証拠パスの逃げや別ウィンドウへの参照を拒否する(string path)
    {using var c=new Context();c.Reject(c.Manifest(new[]{c.Row with{ImageFile=path}}));}
    [Fact]
    public void 成果物の改変や寸法偽装を拒否する()
    {
        using var c=new Context();var manifest=c.Manifest();
        c.Reject(c.Manifest(new[]{c.Row with{Width=4097}}));
        File.AppendAllText(Path.Combine(c.Root,c.Row.UiFile),"tamper");c.Reject(manifest);
    }
    [Fact]
    public void ディレクトリリンク経由の成果物を拒否する()
    {
        using var c=new Context();string source=Path.Combine(c.Root,"windows"),target=Path.Combine(c.Root,"redirected");Directory.Move(source,target);
        string Quote(string value)=>"'"+value.Replace("'","''")+"'";
        var start=new ProcessStartInfo("pwsh"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in new[]{"-NoProfile","-Command","New-Item -ItemType Junction -Path "+Quote(source)+" -Target "+Quote(target)+" | Out-Null"})start.ArgumentList.Add(arg);
        using var process=Process.Start(start);Assert.True(process.WaitForExit(10000));Assert.Equal(0,process.ExitCode);
        c.Reject(c.Manifest());
    }
    [Fact]
    public void 公開領域外の画素や全黒画像の公開偽装を拒否する()
    {
        using var c=new Context(true);c.Write(true,new Rectangle(0,0,1,1));
        GroupEvidenceValidation.Verify(c.Work,c.Manifest());
        var bytes=EvidenceFiles.OpaquePng(new Size(2,2));EvidenceFiles.AtomicBytes(Path.Combine(c.Root,c.Row.ImageFile),bytes);
        c.Row=c.Row with{ImageSha256=EvidenceFiles.Hash(bytes)};c.Reject(c.Manifest());
        c.Write(true,new Rectangle(0,0,1,1));
        using var bitmap=new Bitmap(2,2);using(var graphics=Graphics.FromImage(bitmap))graphics.Clear(Color.Blue);
        using var output=new MemoryStream();bitmap.Save(output,ImageFormat.Png);bytes=output.ToArray();
        EvidenceFiles.AtomicBytes(Path.Combine(c.Root,c.Row.ImageFile),bytes);c.Row=c.Row with{ImageSha256=EvidenceFiles.Hash(bytes)};c.Reject(c.Manifest());
    }
    [Fact]
    public void 互換画像は検証済みroot画像か不透明黒だけを許し互換UIにも文字を許可しない()
    {
        using var c=new Context(true);var manifest=c.Manifest();
        File.Copy(Path.Combine(c.Root,c.Row.ImageFile),Path.Combine(c.Root,"frame.png"));
        File.Copy(Path.Combine(c.Root,c.Row.UiFile),Path.Combine(c.Root,"ui.json"));
        GroupEvidenceValidation.Verify(c.Work,manifest);
        EvidenceFiles.AtomicBytes(Path.Combine(c.Root,"frame.png"),EvidenceFiles.OpaquePng(new Size(16,16)));
        GroupEvidenceValidation.Verify(c.Work,manifest);
        using(var bitmap=new Bitmap(2,2))
        {
            using(var graphics=Graphics.FromImage(bitmap))graphics.Clear(Color.Red);
            using var output=new MemoryStream();bitmap.Save(output,ImageFormat.Png);
            EvidenceFiles.AtomicBytes(Path.Combine(c.Root,"frame.png"),output.ToArray());
        }
        c.Reject(manifest);
        EvidenceFiles.AtomicBytes(Path.Combine(c.Root,"frame.png"),EvidenceFiles.OpaquePng(new Size(16,16)));
        EvidenceFiles.AtomicJson(Path.Combine(c.Root,"ui.json"),new EvidenceUi(Array.Empty<EvidenceNode>(),false,TextRead:true));
        c.Reject(manifest);
    }
    [Fact]
    public void 必須窓の欠落や時刻ずれや未確定な集合は要件未達とする()
    {
        using var c=new Context();var second=c.Window with{WindowId=Guid.NewGuid(),Hwnd=22,OwnerHwnd=11,Relation="owned"};
        var plan=c.Plan with{Snapshot=c.Plan.Snapshot with{Windows=new[]{c.Window,second}},Policy=c.Plan.Policy with{RequirePublicDiagnostic=false,Approvals=new[]{c.Plan.Policy.Approvals[0],new WindowApproval(22,"dialog",true,second.WindowId,1)}}};
        var other=c.Row with{Window=second,Role="dialog",FrameTime100ns=c.Row.FrameTime100ns+30_000_000};
        Assert.False(GroupEvidenceValidation.Evaluate(plan,new[]{c.Row},true).RequirementsMet);
        Assert.False(GroupEvidenceValidation.Evaluate(plan,new[]{c.Row,other},true).RequirementsMet);
        other=other with{FrameTime100ns=c.Row.FrameTime100ns};Assert.True(GroupEvidenceValidation.Evaluate(plan,new[]{c.Row,other},true).RequirementsMet);
        Assert.False(GroupEvidenceValidation.Evaluate(plan,new[]{c.Row,other},false).RequirementsMet);
        Assert.False(GroupEvidenceValidation.Evaluate(plan with{Snapshot=plan.Snapshot with{Truncated=true}},new[]{c.Row,other},true).RequirementsMet);
    }
    [Fact]
    public void 明示承認した同PID子だけを親画像に含まれる構造情報として扱う()
    {
        using var c=new Context();var child=c.Window with{WindowId=Guid.NewGuid(),Hwnd=22,ParentHwnd=11,IsChild=true,Relation="child"};
        var plan=c.Plan with{Snapshot=c.Plan.Snapshot with{Windows=new[]{c.Window,child}},Policy=c.Plan.Policy with{RequirePublicDiagnostic=false,Approvals=new[]{c.Plan.Policy.Approvals[0],new WindowApproval(22,"contained",false,child.WindowId,1)}}};
        var contained=new WindowEvidence(child,"contained","structure-only",null,null,0,0,null,null,null,null,"not-captured","opaque-only",false,"included-in-parent");
        Assert.True(GroupEvidenceValidation.Evaluate(plan,new[]{c.Row,contained},true).RequirementsMet);
        var unbound=plan with{Policy=plan.Policy with{Approvals=new[]{plan.Policy.Approvals[0]}}};
        Assert.False(GroupEvidenceValidation.Evaluate(unbound,new[]{c.Row,contained},true).RequirementsMet);
        var foreign=child with{Pid=88,Relation="foreign"};var badPlan=plan with{Snapshot=plan.Snapshot with{Windows=new[]{c.Window,foreign}}};
        Assert.False(GroupEvidenceValidation.Evaluate(badPlan,new[]{c.Row,contained with{Window=foreign}},true).RequirementsMet);
    }
}
