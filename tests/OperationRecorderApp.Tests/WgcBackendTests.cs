using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

public sealed class WgcBackendTests
{
    [Theory]
    [InlineData(0,1)] [InlineData(1,0)] [InlineData(4097,1)] [InlineData(2049,2049)]
    public void 範囲外サイズを確保前に拒否する(int width,int height)
        => Assert.Throws<EvidenceUnavailableException>(()=>WgcCaptureBackend.ValidateSize(width,height));

    [Fact]
    public async Task 不正HWNDを撮影せず拒否する()
    {
        var error=await Assert.ThrowsAsync<EvidenceUnavailableException>(()=>new WgcCaptureBackend().CaptureAsync(0,DateTimeOffset.UtcNow.AddSeconds(1),CancellationToken.None));
        Assert.Equal("target-mismatch",error.Code);
    }
    [Fact]
    public void QPC時刻は100ns単位で単調増加する()
    {
        long first=WgcCaptureBackend.Clock100ns();Thread.Sleep(10);long second=WgcCaptureBackend.Clock100ns();
        Assert.InRange(second-first,10000,10000000);
    }

    // Uses only forms created here. No keyboard/mouse injection, foreground changes, or screen capture.
    [Fact,Trait("Category","Desktop")]
    public async Task 専用窓の非アクティブ遮蔽非表示と子HWNDをWGCで実測する()
    {
        if(!WgcCaptureBackend.IsInputDesktopAvailable())
        {
            WriteObservations(new System.Collections.Generic.Dictionary<string,string>{["environment"]="input-desktop-unavailable",["inactive"]="not-tested",["occluded-updated"]="not-tested",["minimized"]="not-tested",["hidden"]="not-tested",["offscreen"]="not-tested",["child-direct"]="not-tested"});
            throw new InvalidOperationException("WGC_BLOCKED: 入力デスクトップの読取り条件を満たさず、WGC nativeの有効性は未証明です。");
        }
        using var fixture=new WgcFixture();
        var backend=new WgcCaptureBackend();
        var observed=new ConcurrentDictionary<string,string>();
        foreach(string state in new[]{"cloaked","virtual-desktop","locked","disconnected","multi-dpi"})observed[state]="not-tested";
        using(var first=await backend.CaptureAsync(fixture.Handle,DateTimeOffset.UtcNow.AddSeconds(8),CancellationToken.None))
        {
            Assert.True(first.FrameTime100ns>0);Assert.Equal(first.Bitmap.Size,first.ContentSize);
            Assert.Contains(Color.Red.ToArgb(),PixelColors(first.Bitmap)); observed["inactive"]="captured";
        }
        await fixture.Change(Color.Blue,true);
        using(var changed=await backend.CaptureAsync(fixture.Handle,DateTimeOffset.UtcNow.AddSeconds(8),CancellationToken.None))
        { Assert.Contains(Color.Blue.ToArgb(),PixelColors(changed.Bitmap)); observed["occluded-updated"]="captured"; }
        async Task Observe(string name,long hwnd)
        {
            try { using var frame=await backend.CaptureAsync(hwnd,DateTimeOffset.UtcNow.AddSeconds(2),CancellationToken.None);observed[name]="captured"; }
            catch(EvidenceUnavailableException error) {observed[name]=error.Code;}
        }
        await Observe("child-direct",fixture.ChildHandle);
        await fixture.NativeState(6); await Observe("minimized",fixture.Handle);
        await fixture.NativeState(0); await Observe("hidden",fixture.Handle);
        await fixture.NativeState(4); await fixture.MoveOutside(); await Observe("offscreen",fixture.Handle);
        Assert.All(observed.Values,value=>Assert.Contains(value,new[]{"captured","capture-unavailable","capture-denied","capture-unsupported","target-mismatch","deadline-exceeded","capture-size-changed"}));
        // Preserve only public status codes; raw frames and titles are never persisted by these tests.
        WriteObservations(observed);
    }
    static void WriteObservations(System.Collections.Generic.IDictionary<string,string> values)
    { string path=Environment.GetEnvironmentVariable("OPREC_WGC_OBSERVATIONS");if(!string.IsNullOrEmpty(path))System.IO.File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(values)); }
    static int[] PixelColors(Bitmap bitmap)
    {
        var colors=new System.Collections.Generic.HashSet<int>();
        for(int y=0;y<bitmap.Height;y+=5)for(int x=0;x<bitmap.Width;x+=5)colors.Add(bitmap.GetPixel(x,y).ToArgb());return colors.ToArray();
    }
    sealed class WgcFixture:IDisposable
    {
        readonly Thread thread;
        readonly TaskCompletionSource<bool> ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
        Form window,cover;Panel child;
        public long Handle{get;private set;}
        public long ChildHandle{get;private set;}
        [DllImport("user32.dll")]static extern bool ShowWindow(IntPtr hwnd,int command);
        public WgcFixture()
        {
            thread=new Thread(()=>
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                window=new InactiveForm { Text="WGC 専用fixture",StartPosition=FormStartPosition.Manual,Location=new Point(80,80),ClientSize=new Size(300,220),BackColor=Color.Red,ShowInTaskbar=false };
                child=new Panel{Bounds=new Rectangle(15,15,100,80),BackColor=Color.Lime};window.Controls.Add(child);
                window.Shown+=(_,_)=>{Handle=window.Handle.ToInt64();ChildHandle=child.Handle.ToInt64();ready.SetResult(true);};
                Application.Run(window);
            }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        Task Invoke(Action action)
        {var done=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);window.BeginInvoke(()=>{try{action();done.SetResult(true);}catch(Exception e){done.SetException(e);}});return done.Task;}
        public Task Change(Color color,bool occlude)=>Invoke(()=>{window.BackColor=color;window.Refresh();if(occlude){cover=new InactiveForm{StartPosition=FormStartPosition.Manual,Bounds=window.Bounds,BackColor=Color.Magenta,ShowInTaskbar=false};cover.Show();}});
        public Task NativeState(int state)=>Invoke(()=>{cover?.Hide();ShowWindow(window.Handle,state);});
        public Task MoveOutside()=>Invoke(()=>window.Location=new Point(-20000,-20000));
        public void Dispose(){if(window!=null&&!window.IsDisposed){try{Invoke(()=>{cover?.Close();window.Close();}).Wait(TimeSpan.FromSeconds(3));}catch{}}thread.Join(3000);}
        sealed class InactiveForm:Form{protected override bool ShowWithoutActivation=>true;}
    }
}
