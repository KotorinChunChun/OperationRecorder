using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

[Trait("Category","Desktop")]
public sealed class WindowGraphNativeTests
{
    sealed class QuietWindow : Form
    {
        protected override bool ShowWithoutActivation => true;
        public QuietWindow() { ShowInTaskbar=false; StartPosition=FormStartPosition.Manual; Bounds=new Rectangle(-18000,-18000,360,260); }
    }
    static void Sta(Action body)
    {
        Exception error=null;
        var thread=new Thread(()=> { try{body();}catch(Exception ex){error=ex;} });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(15000),"Dedicated window graph fixture did not finish");
        if(error!=null) ExceptionDispatchInfo.Capture(error).Throw();
    }
    static EvidenceTarget Target(Form form)
    {
        using var process=Process.GetCurrentProcess();
        return new(process.Id,process.StartTime.ToUniversalTime(),process.MainModule.FileName,process.SessionId,form.Handle.ToInt64());
    }
    static WindowGraphSnapshot Snapshot(WindowGraphRegistry registry,EvidenceTarget root)
    {
        var result=registry.Snapshot(root,128);
        var wait=Stopwatch.StartNew();
        while(result.Truncated && wait.ElapsedMilliseconds<2000) { Thread.Sleep(20);result=registry.Snapshot(root,128); }
        return result;
    }
    [Fact]
    public void 専用窓の親子と所有と無所有を区別しHideと最小化を識別失敗にしない()
    {
        Sta(()=>
        {
            using var root=new QuietWindow();
            using var child=new Panel { Bounds=new Rectangle(300,30,140,80) };
            root.Controls.Add(child);root.Show();child.CreateControl();
            using var owned=new QuietWindow { Location=new Point(-17600,-18000) }; owned.Show(root);
            using var ownedChild=new Panel { Bounds=new Rectangle(10,10,60,40) };owned.Controls.Add(ownedChild);ownedChild.CreateControl();
            using var unowned=new QuietWindow { Location=new Point(-17200,-18000) }; unowned.Show();
            Application.DoEvents();
            var target=Target(root);
            using var registry=new WindowGraphRegistry();
            Assert.True(registry.MatchesProcess(target));
            var graph=Snapshot(registry,target);
            var main=Assert.Single(graph.Windows,w=>w.Hwnd==root.Handle.ToInt64());Assert.Equal("root",main.Relation);
            var nested=Assert.Single(graph.Windows,w=>w.Hwnd==child.Handle.ToInt64());Assert.Equal("child",nested.Relation);Assert.True(nested.IsChild);Assert.Equal(root.Handle.ToInt64(),nested.ParentHwnd);Assert.False(nested.Bounds.IsEmpty);
            var dialog=Assert.Single(graph.Windows,w=>w.Hwnd==owned.Handle.ToInt64());Assert.Equal("owned",dialog.Relation);Assert.Equal(root.Handle.ToInt64(),dialog.OwnerHwnd);Assert.Equal(0,dialog.ParentHwnd);
            Assert.Equal("child",graph.Windows.Single(w=>w.Hwnd==ownedChild.Handle.ToInt64()).Relation);
            Assert.Equal("unowned",graph.Windows.Single(w=>w.Hwnd==unowned.Handle.ToInt64()).Relation);
            // This only changes our dedicated fixture. No focus or physical input is requested.
            root.WindowState=FormWindowState.Minimized;Application.DoEvents();Assert.True(registry.MatchesProcess(target));
            Assert.True(Snapshot(registry,target).Windows.Single(w=>w.Hwnd==target.WindowHandle).Minimized);
            root.Hide();Application.DoEvents();Assert.True(registry.MatchesProcess(target));
            Assert.False(Snapshot(registry,target).Windows.Single(w=>w.Hwnd==target.WindowHandle).Visible);
            Assert.False(registry.MatchesProcess(target with {ProcessStartTimeUtc=target.ProcessStartTimeUtc.AddTicks(1)}));
            Assert.False(registry.MatchesProcess(target with {WindowsSessionId=target.WindowsSessionId+1}));
            root.Dispose();Assert.False(registry.MatchesProcess(target));
        });
    }
    [Fact]
    public void 専用の所有変更は新しい世代となり上限到達を完全取得としない()
    {
        Sta(()=>
        {
            using var root=new QuietWindow();root.Show();
            using var popup=new QuietWindow();popup.Show(root);Application.DoEvents();
            var target=Target(root);using var registry=new WindowGraphRegistry();
            var before=Snapshot(registry,target).Windows.Single(w=>w.Hwnd==popup.Handle.ToInt64());
            popup.Owner=null;Application.DoEvents();
            var after=Snapshot(registry,target).Windows.Single(w=>w.Hwnd==popup.Handle.ToInt64());
            Assert.Equal("unowned",after.Relation);Assert.NotEqual(before.WindowId,after.WindowId);Assert.True(after.Generation>before.Generation);
            var truncated=registry.Snapshot(target,1);Assert.True(truncated.Truncated);Assert.Single(truncated.Windows);Assert.Equal(target.WindowHandle,truncated.Windows[0].Hwnd);
        });
    }
}
