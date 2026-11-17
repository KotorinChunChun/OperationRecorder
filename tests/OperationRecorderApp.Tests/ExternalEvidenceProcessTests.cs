using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

public sealed class ExternalEvidenceProcessTests
{
    public static string FixtureExe
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            string config = current.Name;
            for (var p = current; p != null; p = p.Parent)
            {
                string file = Path.Combine(p.FullName,"OperationRecorder.EvidenceFixture", config,"OperationRecorder.EvidenceFixture.exe");
                if (File.Exists(file)) return file;
            }
            throw new FileNotFoundException("Evidence fixture not built");
        }
    }
    static Process Start(params string[] arguments)
    {
        var info = new ProcessStartInfo(FixtureExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        return Process.Start(info);
    }
    [Theory]
    [InlineData("--worker-hang")]
    [InlineData("--worker-save-hang")]
    public async Task 実ワーカーの停止をプロセス境界で期限内に打ち切る(string mode)
    {
        string root = Path.Combine(Path.GetTempPath(), "oprec-worker-test-"+Guid.NewGuid().ToString("N"));
        try
        {
            var backend = new EvidenceWorkerBackend(() => { var info = new ProcessStartInfo(FixtureExe); info.ArgumentList.Add(mode); return info; });
            var work = new CaptureWork(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"before",new(1,DateTimeOffset.UtcNow,"C:/fixture.exe",1,2),new(),root,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddMilliseconds(800));
            var watch = Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<EvidenceUnavailableException>(() => backend.CaptureAndSaveAsync(work,CancellationToken.None));
            Assert.Equal("deadline-exceeded", error.Code); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4));
            if (mode == "--worker-save-hang") Assert.True(File.Exists(Path.Combine(root,"commit.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root,true); }
    }
    [Fact]
    public async Task ワーカー異常終了の生例外やstderrを外へ出さない()
    {
        var backend = new EvidenceWorkerBackend(() => { var info = new ProcessStartInfo(FixtureExe); info.ArgumentList.Add("--worker-crash"); return info; });
        var work = new CaptureWork(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"before",null,new(),Path.GetTempPath(),DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddSeconds(4));
        var error = await Assert.ThrowsAsync<EvidenceUnavailableException>(() => backend.CaptureAndSaveAsync(work,CancellationToken.None));
        Assert.Equal("worker-unavailable", error.Code); Assert.DoesNotContain("DUMMY_SECRET",error.ToString());
    }
    [Fact]
    public async Task 実IPCで保存完了応答の相関を確認しクライアント期限後は成功扱いしない()
    {
        using var owner = Process.GetCurrentProcess(); string pipe = "oprec-evidence-"+Guid.NewGuid().ToString("N");
        using var broker = Start("--host",pipe,owner.Id.ToString(),owner.StartTime.ToUniversalTime().Ticks.ToString());
        string root = Path.Combine(Path.GetTempPath(),"oprec-ipc-test-"+Guid.NewGuid().ToString("N"));
        using var client = new EvidenceConnection(pipe);
        try
        {
            await client.ConnectAsync(); Guid id = Guid.NewGuid();
            EvidenceRequest Request(string method) => new() { RequestId = Guid.NewGuid(),SessionId=id,DeadlineUtc=DateTimeOffset.UtcNow.AddSeconds(5),Method=method };
            var start = await client.SendAsync(Request("StartSession") with { Target = new(99,DateTimeOffset.UtcNow,"C:/fixture.exe",1,2),OutputDirectory=root }); Assert.Equal("started", start.Status);
            var request = Request("BeginStep") with { StepId=Guid.NewGuid(),OperationName="invoke" };
            var watch=Stopwatch.StartNew(); var response = await client.SendAsync(request,200);
            Assert.Equal(request.RequestId,response.RequestId); Assert.Equal("response-unconfirmed", response.Status); Assert.False(response.OperationAllowed);
            Assert.True(watch.Elapsed<TimeSpan.FromSeconds(2));
        }
        finally { client.Dispose(); if (!broker.HasExited) broker.Kill(true); await broker.WaitForExitAsync(); if(Directory.Exists(root)) Directory.Delete(root,true); }
    }
    [Fact]
    public async Task 呼出し元の死亡後に専用brokerが残留しない()
    {
        using var owner=Start("--spawn-broker-then-exit");
        string line=await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0,owner.ExitCode);
        try { using var broker=Process.GetProcessById(int.Parse(line)); await broker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch(ArgumentException) { } // Already exited before the query.
    }
    [Fact]
    public async Task 同一ユーザーでも別PIDのクライアントを拒否する()
    {
        using var allowed=Start("--idle");
        using var owner = Process.GetCurrentProcess(); string pipe="oprec-evidence-"+Guid.NewGuid().ToString("N");
        using var broker=Start("--host",pipe,allowed.Id.ToString(),allowed.StartTime.ToUniversalTime().Ticks.ToString());
        using var client = new EvidenceConnection(pipe);
        try
        {
            await client.ConnectAsync(); var response=await client.SendAsync(new EvidenceRequest { RequestId=Guid.NewGuid(),SessionId=Guid.NewGuid(),Method="Flush",DeadlineUtc=DateTimeOffset.UtcNow.AddSeconds(2) });
            Assert.False(response.OperationAllowed); Assert.Equal("response-unconfirmed",response.Status);
        }
        finally { if(!allowed.HasExited) allowed.Kill(true); if(!broker.HasExited) broker.Kill(true); }
    }
    sealed class TrapElement : IProtectedEvidenceElement
    {
        public int NameReads, ChildReads;
        public bool Password, IsForeign;
        public TrapElement Child, Sibling;
        public bool IsPassword => Password;
        public int ProcessId => IsForeign ? 88 : 77;
        public int ControlTypeId => Password ? 50004 : 50025;
        public Rectangle Bounds => new(0,0,20,20);
        public bool Enabled => true;
        public string Name { get { NameReads++; throw new Exception("DUMMY_SECRET_NAME"); } }
        public IProtectedEvidenceElement FirstChild() { ChildReads++; return Child; }
        public IProtectedEvidenceElement NextSibling() => Sibling;
    }
    [Fact]
    public void 認証と未知の要素のNameを読まず認証サブツリーへ降りない()
    {
        var password = new TrapElement {Password=true,Child=new TrapElement()}; var unknown = new TrapElement(); var foreign = new TrapElement {IsForeign=true};
        password.Sibling=unknown; unknown.Sibling=foreign;
        var root=new TrapElement{Child=password};
        var ui=ProtectedEvidenceUi.Collect(root,77,new Rectangle(0,0,80,60),new(),CancellationToken.None);
        Assert.Equal(3,ui.Elements.Count); Assert.True(ui.Truncated); Assert.Equal(0,password.ChildReads);
        Assert.All(new[]{root,password,unknown,foreign},e=>Assert.Equal(0,e.NameReads)); Assert.False(ui.TextRead);
        var limited=ProtectedEvidenceUi.Collect(root,77,new Rectangle(0,0,80,60),new(MaxElements:1),CancellationToken.None);
        Assert.Single(limited.Elements); Assert.True(limited.Truncated);
        var source=JsonSerializer.Serialize(ui); Assert.DoesNotContain("DUMMY_SECRET",source);
    }
    [Fact]
    public async Task 過大IPCと秘密の環境継承を拒否する()
    {
        byte[] prefix=new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix,EvidenceProtocol.MaxMessageBytes+1);
        using var stream=new MemoryStream(prefix);
        await Assert.ThrowsAsync<EvidenceUnavailableException>(()=>EvidenceWire.ReadAsync<EvidenceRequest>(stream,CancellationToken.None));
        var info=new ProcessStartInfo("fixture.exe"); info.Environment["TEST_SECRET"]="DUMMY_SECRET";
        EvidenceProcessEnvironment.Scrub(info); Assert.False(info.Environment.ContainsKey("TEST_SECRET"));
    }
    [Fact]
    public async Task broker停止後もbefore成果物を読取専用で検査し再操作を許可しない()
    {
        using var c=new ExternalEvidenceSessionTests.Context(); await c.Start();
        var begin=await c.Session.HandleAsync(c.Request("BeginStep",Guid.NewGuid()));
        using var inspection=new EvidenceSession(new ExternalEvidenceSessionTests.Backend(),new ExternalEvidenceSessionTests.Validator());
        var result=await inspection.HandleAsync(c.Request("InspectSession") with {OutputDirectory=Path.Combine(c.Root,"output")});
        Assert.Equal("inspected",result.Status); Assert.False(result.OperationAllowed); Assert.Contains(begin.StepId,result.PendingSteps);
        var state=Assert.Single(result.StepStates); Assert.True(state.BeforeSaved); Assert.False(state.AfterSaved); Assert.Equal("indeterminate",state.Status);
    }
}
