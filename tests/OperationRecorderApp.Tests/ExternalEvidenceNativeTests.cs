using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

[Trait("Category", "Desktop")]
public sealed class ExternalEvidenceNativeTests
{
    sealed class Window : IDisposable
    {
        public readonly Process Process;
        public readonly EvidenceTarget Target;
        public Window()
        {
            var start = new ProcessStartInfo(ExternalEvidenceProcessTests.FixtureExe) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
            start.ArgumentList.Add("--window"); Process=System.Diagnostics.Process.Start(start);
            string line=Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            using var json=JsonDocument.Parse(line);
            Target=new(Process.Id,Process.StartTime.ToUniversalTime(),Process.MainModule.FileName,Process.SessionId,json.RootElement.GetProperty("hwnd").GetInt64());
        }
        public void Dispose() { try{if(!Process.HasExited) Process.Kill(true);}catch{} Process.Dispose(); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 専用UIA操作の前後を実ワーカーで保存し秘密をPNGとMP4へ残さない(bool video)
    {
        using var fixture=new Window();
        string encoder=Environment.GetEnvironmentVariable("OPREC_EVIDENCE_TEST_FFMPEG");
        if(video) Assert.True(File.Exists(encoder),"実コーデック検証にはOPREC_EVIDENCE_TEST_FFMPEGが必要です。未実行を成功扱いしません。");
        string root=Path.Combine(Path.GetTempPath(),"oprec-evidence-native-"+Guid.NewGuid().ToString("N"));
        using var client=await EvidenceConnection.StartAsync(Path.Combine(AppContext.BaseDirectory,"OperationRecorder.exe"));
        Guid id=Guid.NewGuid();
        EvidenceRequest Request(string method) => new(){RequestId=Guid.NewGuid(),SessionId=id,Method=method,DeadlineUtc=DateTimeOffset.UtcNow.AddSeconds(15)};
        try
        {
            var start=await client.SendAsync(Request("StartSession") with {Target=fixture.Target,OutputDirectory=root,Policy=new(RecordVideo:video,FfmpegPath:encoder)},20000);
            Assert.Equal("started",start.Status);
            var begin=await client.SendAsync(Request("BeginStep") with{StepId=Guid.NewGuid(),OperationName="invoke"},20000);
            Assert.True(begin.OperationAllowed,begin.Status); Assert.Equal("before-saved",begin.Status);
            var window=AutomationElement.FromHandle(new IntPtr(fixture.Target.WindowHandle));
            var button=window.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"fixtureAction"));
            Assert.NotNull(button); Assert.Equal(fixture.Target.Pid,button.Current.ProcessId);
            ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); // Explicit target only, no global input.
            var state=window.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"fixtureState"));
            Assert.Equal("操作後",state.Current.Name);
            var end=await client.SendAsync(Request("EndStep") with{StepToken=begin.StepToken,Outcome="success"},20000);
            Assert.Equal("after-saved",end.Status); Assert.Equal(begin.StepId,end.StepId);
            var stop=await client.SendAsync(Request("StopSession"),20000); Assert.Equal("stopped",stop.Status); Assert.Empty(stop.PendingSteps);
            foreach(string png in Directory.GetFiles(root,"*.png",SearchOption.AllDirectories))
            { using var bitmap=new Bitmap(png); for(int y=0;y<bitmap.Height;y++)for(int x=0;x<bitmap.Width;x++)Assert.Equal(Color.Black.ToArgb(),bitmap.GetPixel(x,y).ToArgb()); }
            foreach(string file in Directory.GetFiles(root,"*",SearchOption.AllDirectories))
            {
                var bytes=File.ReadAllBytes(file);
                foreach(string secret in new[]{"DUMMY_SECRET_PASSWORD_7419","DUMMY_SECRET_UNKNOWN_8520"})
                { Assert.DoesNotContain(secret,Encoding.UTF8.GetString(bytes)); Assert.DoesNotContain(secret,Encoding.Unicode.GetString(bytes)); }
            }
            var videos=Directory.GetFiles(root,"*.mp4",SearchOption.AllDirectories);
            Assert.Equal(video?2:0,videos.Length);
            foreach(string file in videos)
            {
                var decode=new ProcessStartInfo(encoder){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(var arg in new[]{"-loglevel","quiet","-i",file,"-f","rawvideo","-pix_fmt","rgb24","pipe:1"})decode.ArgumentList.Add(arg);
                using var process=Process.Start(decode); using var timeout=new CancellationTokenSource(10000);
                byte[] pixels=await EvidenceWire.ReadBoundedToEndAsync(process.StandardOutput.BaseStream,16777216,timeout.Token);
                await process.WaitForExitAsync(timeout.Token); Assert.Equal(0,process.ExitCode); Assert.NotEmpty(pixels); Assert.All(pixels,b=>Assert.InRange(b,(byte)0,(byte)1));
            }
        }
        finally { client.Dispose(); if(Directory.Exists(root))Directory.Delete(root,true); }
    }
    [Fact]
    public void PID再利用や別セッションやハンドル変更と破棄を拒否する()
    {
        using var fixture=new Window(); using var validator=new WindowsEvidenceValidator();
        Assert.True(validator.Matches(fixture.Target));
        Assert.False(validator.Matches(fixture.Target with{ProcessStartTimeUtc=fixture.Target.ProcessStartTimeUtc.AddTicks(1)}));
        Assert.False(validator.Matches(fixture.Target with{WindowsSessionId=fixture.Target.WindowsSessionId+1}));
        Assert.False(validator.Matches(fixture.Target with{ExePath="C:/not-the-target.exe"}));
        Assert.False(validator.Matches(fixture.Target with{Pid=Environment.ProcessId}));
        Assert.False(validator.Matches(fixture.Target with{WindowHandle=0}));
        fixture.Process.Kill(); fixture.Process.WaitForExit(); Assert.False(validator.Matches(fixture.Target));
    }
}
