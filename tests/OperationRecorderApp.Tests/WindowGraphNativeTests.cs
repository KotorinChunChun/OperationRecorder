using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

[Trait("Category","Desktop")]
public sealed class WindowGraphNativeTests
{
    [DllImport("user32.dll")]
    static extern void NotifyWinEvent(uint eventId, IntPtr window, int objectId, int childId);

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
            var target=Target(root);using var registry=new WindowGraphRegistry(diagnoseWinEvents: true);
            var before=Snapshot(registry,target).Windows.Single(w=>w.Hwnd==popup.Handle.ToInt64());
            popup.Owner=null;Application.DoEvents();
            var after=Snapshot(registry,target).Windows.Single(w=>w.Hwnd==popup.Handle.ToInt64());
            Assert.Equal("unowned",after.Relation);Assert.NotEqual(before.WindowId,after.WindowId);Assert.True(after.Generation>before.Generation);
            var truncated=registry.SnapshotWithRevisionSource(target,1);Assert.True(truncated.Truncated);Assert.Single(truncated.Windows);Assert.Equal(target.WindowHandle,truncated.Windows[0].Hwnd);
            Assert.Contains("window-limit", PasswordLayoutBackdrop.TruncationReasons(truncated));
        });
    }

    [Fact]
    public void 専用フォームの同じWinEventを発生元と比較区間で区別する()
    {
        const uint NameChange = 0x800C;
        Sta(() =>
        {
            using var targetForm = new QuietWindow();
            using var backdrop = new QuietWindow();
            using var targetChild = new Panel { Bounds = new Rectangle(10, 10, 80, 40) };
            using var backdropChild = new Panel { Bounds = new Rectangle(10, 10, 80, 40) };
            targetForm.Controls.Add(targetChild);
            backdrop.Controls.Add(backdropChild);
            targetForm.Show();
            backdrop.Show();
            Application.DoEvents();
            var target = Target(targetForm);
            using var registry = new WindowGraphRegistry(diagnoseWinEvents: true);
            WindowGraphSnapshot initial = registry.SnapshotWithRevisionSource(target, 128);
            Assert.Equal(backdrop.Handle.ToInt64(), PasswordLayoutBackdrop.Select(target, initial).Background?.Hwnd);

            // Explicit synthetic events use the same Win32 source and callback filters as production.
            void Notify(Control window) => NotifyWinEvent(NameChange, window.Handle, 0, 0);
            WindowGraphSnapshot AwaitEvents(WindowGraphSnapshot since, long count)
            {
                WindowGraphSnapshot next = null;
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    next = registry.SnapshotWithRevisionSource(target, 128);
                    var interval = WindowGraphEventInterval.Between(since.RevisionSource, next.RevisionSource);
                    return !next.Truncated && interval.Counts?.Where(entry => entry.EventId == NameChange)
                        .Sum(entry => entry.Count) >= count;
                }, TimeSpan.FromSeconds(3)), "Dedicated window notifications were not counted.");
                return next;
            }

            Notify(targetForm);
            var first = AwaitEvents(initial, 1);
            Notify(targetForm);
            Notify(targetForm);
            Notify(backdrop);
            Notify(targetChild);
            Notify(backdropChild);
            var next = AwaitEvents(first, 5);
            Notify(backdrop);
            AwaitEvents(next, 1);

            var (_, change) = PasswordLayoutBackdrop.ObserveStable(first, () => next, _ => "observed", (_, _) => null);
            Assert.Equal("confirmation-snapshot", change.Stage);
            Assert.Contains("win-event", change.GraphRevisionSources);
            Assert.Equal(next.RevisionSource.WinEventCount - first.RevisionSource.WinEventCount, change.WinEvents.TotalCount);
            Assert.Equal(new[] {
                new WindowGraphEventCount(NameChange, "background", 1),
                new WindowGraphEventCount(NameChange, "background-child", 1),
                new WindowGraphEventCount(NameChange, "target", 2),
                new WindowGraphEventCount(NameChange, "target-child", 1)
            }, change.WinEvents.Counts.Where(entry => entry.EventId == NameChange));
        });
    }
}
