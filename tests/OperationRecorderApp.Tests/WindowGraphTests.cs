using System;
using System.Drawing;
using System.Linq;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

public sealed class WindowGraphTests
{
    static WindowGraphInput Window(long hwnd = 11, long parent = 0, long owner = 0, int pid = 77, bool child = false, string relation = "root") =>
        new(hwnd, parent, owner, pid, child, true, false, false, new Rectangle(10,20,300,200), 96, relation);

    [Fact]
    public void 同じ窓の表示や位置やDPI変化は同一世代で記録し版を進める()
    {
        var tracker = new WindowGraphTracker(); var initial = tracker.Update(new[] { Window() });
        var same = tracker.Update(new[] { Window() }); Assert.Equal(initial.Revision, same.Revision); Assert.Equal(initial.Windows[0],same.Windows[0]);
        var changed = tracker.Update(new[] { Window() with { Visible = false, Minimized = true, Bounds = new(50,60,400,300), Dpi = 144 } });
        Assert.Equal(initial.Windows[0].WindowId, changed.Windows[0].WindowId);
        Assert.Equal(initial.Windows[0].Generation, changed.Windows[0].Generation);
        Assert.True(changed.Revision > initial.Revision); Assert.False(changed.Windows[0].Visible); Assert.Equal(144,changed.Windows[0].Dpi);
    }
    [Theory]
    [InlineData("parent")]
    [InlineData("owner")]
    [InlineData("pid")]
    [InlineData("child")]
    [InlineData("relation")]
    public void 同じHWNDでも識別や所属の変更は旧承認を継承しない(string change)
    {
        var tracker = new WindowGraphTracker(); var before = tracker.Update(new[] { Window() }).Windows[0];
        var next = change switch {
            "parent" => Window() with { ParentHwnd=22 }, "owner" => Window() with {OwnerHwnd=22},
            "pid" => Window() with {Pid=88}, "child" => Window() with {IsChild=true}, _ => Window() with {Relation="unowned"}
        };
        var after = tracker.Update(new[] { next }).Windows[0];
        Assert.NotEqual(before.WindowId,after.WindowId); Assert.Equal(before.Generation+1,after.Generation);
    }
    [Fact]
    public void 破棄直後の同一HWND再利用は同じ形でも新規世代になる()
    {
        var tracker = new WindowGraphTracker(); var before = tracker.Update(new[] { Window() }).Windows[0];
        tracker.MarkDestroyed(11); var after = tracker.Update(new[] { Window() }).Windows[0];
        Assert.NotEqual(before.WindowId,after.WindowId); Assert.Equal(2,after.Generation);
    }
    [Fact]
    public void 完全列挙で消えた窓は再出現時に旧許可を失い切詰めでは破棄と断定しない()
    {
        var tracker = new WindowGraphTracker(); var first = tracker.Update(new[] { Window(), Window(22,owner:11,relation:"owned") });
        var child = first.Windows.Single(w=>w.Hwnd==22);
        var partial = tracker.Update(new[] { Window() },true); Assert.True(partial.Truncated); Assert.Single(partial.Windows);
        Assert.Equal(child.WindowId,tracker.Update(new[] {Window(),Window(22,owner:11,relation:"owned")}).Windows.Single(w=>w.Hwnd==22).WindowId);
        tracker.Update(new[] {Window()});
        var recreated=tracker.Update(new[] {Window(),Window(22,owner:11,relation:"owned")}).Windows.Single(w=>w.Hwnd==22);
        Assert.NotEqual(child.WindowId,recreated.WindowId); Assert.Equal(2,recreated.Generation);
    }
    [Fact]
    public void 親子と所有と無所有と別PID候補の関係を混同しない()
    {
        var tracker = new WindowGraphTracker();
        var graph = tracker.Update(new[] {Window(),Window(22,parent:11,child:true,relation:"child"),Window(33,owner:11,relation:"owned"),
            Window(44,relation:"unowned"),Window(55,parent:11,pid:88,child:true,relation:"foreign")});
        Assert.Equal(new[]{"root","child","owned","unowned","foreign"},graph.Windows.Select(w=>w.Relation));
        Assert.Equal(0,graph.Windows[1].OwnerHwnd);Assert.Equal(0,graph.Windows[2].ParentHwnd);
    }
    [Fact]
    public void 消えた窓の世代履歴も上限を持ち再利用監視を無制限に増やさない()
    {
        var tracker=new WindowGraphTracker(maxTrackedHandles:2);
        tracker.Update(new[]{Window(11)});tracker.Update(new[]{Window(22)});
        var error=Assert.Throws<EvidenceUnavailableException>(()=>tracker.Update(new[]{Window(33)}));
        Assert.Equal("window-history-limit",error.Code);
        Assert.Single(tracker.Update(new[]{Window(11)}).Windows);
    }
    [Fact]
    public void 重複や無効なHWNDや過大な観測を受理しない()
    {
        var tracker = new WindowGraphTracker();
        Assert.Throws<ArgumentException>(()=>tracker.Update(new[]{Window(),Window()}));
        Assert.Throws<ArgumentException>(()=>tracker.Update(new[]{Window(0)}));
        Assert.Throws<ArgumentException>(()=>tracker.Update(Enumerable.Range(1,1025).Select(n=>Window(n)).ToArray()));
    }
}
