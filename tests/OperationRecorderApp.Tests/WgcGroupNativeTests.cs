using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

[Trait("Category","Desktop")]
public sealed class WgcGroupNativeTests
{
    [Fact]
    public async Task 実brokerとworkerで公開profile未指定の工程を拒否し保護証拠を確定する()
    {
        var info=new ProcessStartInfo(ExternalEvidenceProcessTests.FixtureExe)
        {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        info.ArgumentList.Add("--window");
        using var fixture=Process.Start(info);
        string line=await fixture.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var startup=JsonDocument.Parse(line);
        var target=new EvidenceTarget(fixture.Id,fixture.StartTime.ToUniversalTime(),fixture.MainModule.FileName,
            fixture.SessionId,startup.RootElement.GetProperty("hwnd").GetInt64());
        string artifactBase=Environment.GetEnvironmentVariable("OPREC_WGC_GROUP_ARTIFACTS");
        bool preserve=!string.IsNullOrEmpty(artifactBase);
        string root=Path.Combine(artifactBase??Path.GetTempPath(),"wgc-group-negative-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string sessionPath=Path.Combine(root,"session");
        using var client=await EvidenceConnection.StartAsync(Path.Combine(AppContext.BaseDirectory,"OperationRecorder.exe"));
        using var broker=Process.GetProcessById(client.BrokerPid);
        _=broker.StartTime; // Bind the owned process handle before cleanup; never select a later reused PID.
        Guid sessionId=Guid.NewGuid();
        EvidenceRequest Request(string method)=>new(){SchemaVersion=2,RequestId=Guid.NewGuid(),SessionId=sessionId,
            Method=method,DeadlineUtc=DateTimeOffset.UtcNow.AddSeconds(15)};
        int actions=0;
        try
        {
            await WaitForStableFixtureAsync(target,Path.Combine(root,"fixture-readiness.json"));
            var started=await client.SendAsync(Request("StartSession") with{Target=target,OutputDirectory=sessionPath,GroupPolicy=new()},20000);
            File.WriteAllText(Path.Combine(root,"start-response.json"),JsonSerializer.Serialize(started,EvidenceProtocol.Json));
            Assert.Equal("started",started.Status);
            Assert.Contains("wgc-required",started.Capabilities);Assert.Equal(2,started.SchemaVersion);
            var observed=await client.SendAsync(Request("ListWindows"),20000);
            Assert.Equal("observed",observed.Status);Assert.NotNull(observed.WindowGraph);
            Assert.Contains(observed.WindowGraph.Windows,w=>w.Hwnd==target.WindowHandle&&w.Pid==target.Pid);
            Guid step=Guid.NewGuid();
            var before=await client.SendAsync(Request("BeginStep") with{StepId=step,OperationName="invoke"},20000);
            File.WriteAllText(Path.Combine(root,"before-response.json"),JsonSerializer.Serialize(before,EvidenceProtocol.Json));
            if(before.OperationAllowed) actions++; // No UI operation is sent, even if the guard is broken.
            Assert.Equal(0,actions);Assert.False(before.OperationAllowed);
            Assert.Equal("before-inadequate",before.Status);
            Assert.NotNull(before.Quality);Assert.False(before.Quality.RequirementsMet);
            Assert.Equal("insufficient",before.Quality.DiagnosticStatus);
            Assert.Contains(step,before.PendingSteps);Assert.NotNull(before.SavedAt);
            string commitFile=Assert.Single(before.EvidencePaths);Assert.True(File.Exists(commitFile));
            string phase=Path.GetDirectoryName(commitFile);
            var commit=JsonSerializer.Deserialize<EvidenceCommit>(File.ReadAllBytes(commitFile),EvidenceProtocol.Json);
            Assert.Equal(sessionId,commit.SessionId);Assert.Equal(step,commit.StepId);Assert.Equal("before",commit.Phase);
            Assert.Equal(EvidenceFiles.HashFile(Path.Combine(phase,"group.json")),commit.GroupSha256);
            Assert.Equal(EvidenceFiles.HashFile(Path.Combine(phase,"frame.png")),commit.ImageSha256);
            Assert.Equal(EvidenceFiles.HashFile(Path.Combine(phase,"ui.json")),commit.UiSha256);
            var group=JsonSerializer.Deserialize<GroupEvidenceManifest>(File.ReadAllBytes(Path.Combine(phase,"group.json")),EvidenceProtocol.Json);
            Assert.False(group.Quality.RequirementsMet);Assert.Equal(step,group.StepId);
            Assert.Contains(group.Windows,w=>w.Window.Hwnd==target.WindowHandle);
            foreach(var window in group.Windows)
            {
                Assert.False(window.RequirementsMet);
                if(window.ImageFile!=null)Assert.Equal(EvidenceFiles.HashFile(Path.Combine(phase,window.ImageFile)),window.ImageSha256);
                if(window.UiFile!=null)Assert.Equal(EvidenceFiles.HashFile(Path.Combine(phase,window.UiFile)),window.UiSha256);
            }
            foreach(string png in Directory.GetFiles(sessionPath,"*.png",SearchOption.AllDirectories))
            {using var bitmap=new Bitmap(png);for(int y=0;y<bitmap.Height;y++)for(int x=0;x<bitmap.Width;x++)Assert.Equal(Color.Black.ToArgb(),bitmap.GetPixel(x,y).ToArgb());}
            foreach(string file in Directory.GetFiles(sessionPath,"*",SearchOption.AllDirectories).Where(p=>!p.EndsWith("external.lock")))
            {
                byte[] bytes=File.ReadAllBytes(file);
                foreach(string secret in new[]{"DUMMY_SECRET_PASSWORD_7419","DUMMY_SECRET_UNKNOWN_8520"})
                {Assert.DoesNotContain(secret,Encoding.UTF8.GetString(bytes));Assert.DoesNotContain(secret,Encoding.Unicode.GetString(bytes));}
            }
            var stopped=await client.SendAsync(Request("StopSession"),20000);
            Assert.Equal("stopped",stopped.Status);Assert.Contains(step,stopped.PendingSteps);Assert.False(stopped.OperationAllowed);
            File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{schemaVersion=1,result="negative-contract-passed",
                before.Status,before.OperationAllowed,actions,quality=before.Quality,commit.GroupSha256,commit.ImageSha256,commit.UiSha256},EvidenceProtocol.Json));
        }
        finally
        {
            client.Dispose();
            try { await broker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            finally
            {
                try{if(!fixture.HasExited){fixture.Kill(true);await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));}}catch{}
                if(!preserve&&Directory.Exists(root))Directory.Delete(root,true);
            }
        }
    }

    static async Task WaitForStableFixtureAsync(EvidenceTarget target,string logPath)
    {
        // Shown reports the HWND before the initial paint/accessibility events have settled.
        // Observe only this owned fixture; never retry or weaken a rejected StartSession.
        using var graph=new WindowGraphRegistry();
        var watch=Stopwatch.StartNew();
        var observations=new List<FixtureReadiness>();
        WindowGraphSnapshot previous=null;
        long stableSince=0;
        bool ready=false;
        string error=null;
        try
        {
            while(watch.ElapsedMilliseconds<5000)
            {
                var current=graph.Snapshot(target);
                long elapsed=watch.ElapsedMilliseconds;
                observations.Add(new(elapsed,current.Revision,current.Truncated,current.Windows.Count));
                bool valid=!current.Truncated&&current.Windows.Any(w=>w.Hwnd==target.WindowHandle&&w.Pid==target.Pid&&
                    w.Relation=="root"&&w.Visible&&!w.Minimized&&w.Bounds.Width>0&&w.Bounds.Height>0&&w.Dpi>0);
                bool unchanged=valid&&previous!=null&&!previous.Truncated&&current.Revision==previous.Revision&&
                    current.Windows.OrderBy(w=>w.Hwnd).SequenceEqual(previous.Windows.OrderBy(w=>w.Hwnd));
                if(!unchanged)stableSince=elapsed;
                if(unchanged&&elapsed-stableSince>=250){ready=true;return;}
                previous=valid?current:null;
                await Task.Delay(50);
            }
            Assert.Fail("The dedicated fixture did not provide complete, stable window observations within 5 seconds.");
        }
        catch(EvidenceUnavailableException exception){error=exception.Code;throw;}
        finally
        {
            File.WriteAllText(logPath,JsonSerializer.Serialize(new{schemaVersion=1,ready,error,
                elapsedMs=watch.ElapsedMilliseconds,observations},EvidenceProtocol.Json));
        }
    }

    sealed record FixtureReadiness(long ElapsedMs,long Revision,bool Truncated,int Windows);
}
