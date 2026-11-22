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

    [Fact,Trait("Category","Desktop")]
    public async Task 親WGCフレームから専用子領域を切出し既知色を照合する()
    {
        var observed=new ConcurrentDictionary<string,string>();
        observed["parent-crop"]="not-tested";
        try
        {
            if(!WgcCaptureBackend.IsInputDesktopAvailable())
            {
                observed["parent-crop"]="input-desktop-unavailable";
                throw new InvalidOperationException("WGC_BLOCKED: 親cropの取得条件を満たしません。");
            }
            using var fixture=new WgcFixture();
            using var process=System.Diagnostics.Process.GetCurrentProcess();
            var target=new EvidenceTarget(process.Id,process.StartTime.ToUniversalTime(),process.MainModule.FileName,process.SessionId,fixture.Handle);
            using var registry=new WindowGraphRegistry();
            using var frame=await new WgcCaptureBackend().CaptureAsync(fixture.Handle,DateTimeOffset.UtcNow.AddSeconds(8),CancellationToken.None);
            var graph=registry.Snapshot(target,256);
            var parent=graph.Windows.Single(w=>w.Hwnd==fixture.Handle);
            var child=graph.Windows.Single(w=>w.Hwnd==fixture.ChildHandle);
            var rectangle=new Rectangle(child.Bounds.X-parent.Bounds.X,child.Bounds.Y-parent.Bounds.Y,child.Bounds.Width,child.Bounds.Height);
            string measurements=System.Text.Json.JsonSerializer.Serialize(new
            {
                parentBounds=new{parent.Bounds.X,parent.Bounds.Y,parent.Bounds.Width,parent.Bounds.Height},parentDpi=parent.Dpi,parentCloaked=parent.Cloaked,
                childBounds=new{child.Bounds.X,child.Bounds.Y,child.Bounds.Width,child.Bounds.Height},childDpi=child.Dpi,childCloaked=child.Cloaked,childVisible=child.Visible,
                crop=new{rectangle.X,rectangle.Y,rectangle.Width,rectangle.Height},
                contentWidth=frame.ContentSize.Width,contentHeight=frame.ContentSize.Height,graph.Truncated,
                child.IsChild,childParentHwnd=child.ParentHwnd,parentHwnd=parent.Hwnd
            });
            observed["parent-crop-measurements"]=measurements;
            Assert.False(graph.Truncated,measurements);
            Assert.True(child.IsChild && child.ParentHwnd==parent.Hwnd,measurements);
            if(parent.Bounds.Size!=frame.ContentSize)
            {
                observed["parent-crop"]="parent-frame-size-mismatch";
                Assert.Fail(measurements);
            }
            if(rectangle.Width<=0||rectangle.Height<=0||!new Rectangle(Point.Empty,frame.ContentSize).Contains(rectangle))
            {
                observed["parent-crop"]="child-region-clipped";
                Assert.Fail(measurements);
            }
            using var crop=frame.Bitmap.Clone(rectangle,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            Assert.Equal(Color.Lime.ToArgb(),crop.GetPixel(crop.Width/2,crop.Height/2).ToArgb());
            Assert.Contains(Color.Lime.ToArgb(),PixelColors(crop));
            observed["parent-crop"]="captured-known-child-color";
        }
        catch(EvidenceUnavailableException error){observed["parent-crop"]=error.Code;throw;}
        finally{WriteObservations(observed);}
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
        Assert.All(new[]{"inactive","occluded-updated","child-direct","minimized","hidden","offscreen"},
            state=>Assert.Contains(observed[state],new[]{"captured","capture-unavailable","capture-denied","capture-unsupported","target-mismatch","deadline-exceeded","capture-size-changed"}));
        // Preserve only public status codes; raw frames and titles are never persisted by these tests.
        WriteObservations(observed);
    }
    static void WriteObservations(System.Collections.Generic.IDictionary<string,string> values)
    {
        string path=Environment.GetEnvironmentVariable("OPREC_WGC_OBSERVATIONS");
        if(string.IsNullOrEmpty(path))return;
        var merged=System.IO.File.Exists(path)?System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string,string>>(System.IO.File.ReadAllText(path)):new();
        foreach(var pair in values)merged[pair.Key]=pair.Value;
        System.IO.File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(merged));
    }
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
        [DllImport("dwmapi.dll")]static extern int DwmFlush();
        public WgcFixture()
        {
            thread=new Thread(()=>
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                window=new InactiveForm { Text="WGC 専用fixture",StartPosition=FormStartPosition.Manual,Location=new Point(80,80),ClientSize=new Size(300,220),BackColor=Color.Red,ShowInTaskbar=false };
                child=new Panel{Bounds=new Rectangle(15,15,100,80),BackColor=Color.Lime};window.Controls.Add(child);
                window.Shown+=(_,_)=>
                {
                    try
                    {
                        Handle=window.Handle.ToInt64();ChildHandle=child.Handle.ToInt64();
                        // Shown is earlier than the first WM_PAINT. Publish readiness only after
                        // the fixture's known colors have been painted and submitted to DWM.
                        window.Refresh();child.Refresh();window.Update();child.Update();
                        if(DwmFlush()<0)throw new InvalidOperationException("WGC fixture composition unavailable");
                        ready.SetResult(true);
                    }
                    catch(Exception error){ready.TrySetException(error);}
                };
                Application.Run(window);
            }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        Task Invoke(Action action)
        {var done=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);window.BeginInvoke(()=>{try{action();done.SetResult(true);}catch(Exception e){done.SetException(e);}});return done.Task;}
        public Task Change(Color color,bool occlude)=>Invoke(()=>{window.BackColor=color;window.Refresh();if(occlude){cover=new InactiveForm{StartPosition=FormStartPosition.Manual,Bounds=window.Bounds,BackColor=Color.Magenta,ShowInTaskbar=false};cover.Show();}});
        public Task NativeState(int state)=>Invoke(()=>{cover?.Hide();ShowWindow(window.Handle,state);});
        public Task MoveOutside()=>Invoke(()=>window.Location=new Point(-20000,-20000));
        public void Dispose(){if(window!=null&&!window.IsDisposed){try{Invoke(()=>{cover?.Close();window.Close();}).Wait(TimeSpan.FromSeconds(3));}catch{}}thread.Join(3000);}
        sealed class InactiveForm:Form
        {
            [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
            protected override bool ShowWithoutActivation=>true;
            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                int disabled=1;
                // Disable transitions on this synthetic fixture only. A screenshot of a
                // fade-in intermediate cannot establish its intended settled public colors.
                if(DwmSetWindowAttribute(Handle,3 /* DWMWA_TRANSITIONS_FORCEDISABLED */,ref disabled,sizeof(int))<0)
                    throw new InvalidOperationException("WGC fixture transitions unavailable");
            }
        }
    }
}
