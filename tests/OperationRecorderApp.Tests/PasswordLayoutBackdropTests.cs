using System;
using System.Drawing;
using System.Reflection;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

public sealed class PasswordLayoutBackdropTests
{
    static readonly Assembly ApplicationAssembly = typeof(PasswordLayoutCandidate).Assembly;
    static readonly Type Backdrop = ApplicationAssembly.GetType("OperationRecorderApp.ExternalEvidence.PasswordLayoutBackdrop", true);
    static readonly Type LayoutWindow = ApplicationAssembly.GetType("OperationRecorderApp.ExternalEvidence.PasswordLayoutWindow", true);
    static readonly EvidenceTarget Target = new(13000, DateTimeOffset.UnixEpoch, @"C:\fixture.exe", 1, 10);
    const string WindowClass = "WindowsForms10.Window.8.app.0.fixture";
    static readonly string Shape = new('a', 64);

    static object Invoke(string name, params object[] args) => Backdrop.GetMethod(name,
        BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);

    static WindowObservation Window(long hwnd, string role, int pid = 13000, bool cloaked = false, bool visible = true) =>
        new(Guid.NewGuid(), 1, hwnd, 0, 0, pid, false, visible, false, cloaked, new Rectangle(20, 30, 300, 240), 96, role);

    static (WindowObservation Login, WindowObservation Background, string Reason) Select(params WindowObservation[] windows) =>
        ((WindowObservation, WindowObservation, string))Invoke("Select", Target, new WindowGraphSnapshot(1, false, windows));

    [Fact]
    public void Only_one_visible_same_process_top_level_is_a_backdrop()
    {
        var login = Window(10, "root");
        var behind = Window(20, "unowned");
        var helper = Window(30, "unowned", visible: false);
        Assert.Equal((login, behind, (string)null), Select(login, behind, helper));
        Assert.Equal("background-window-missing", Select(login, helper).Reason);
        Assert.Equal("ambiguous-window-layout", Select(login, behind, Window(40, "unowned")).Reason);
        Assert.Equal("foreign-window", Select(login, behind, Window(40, "foreign", pid: 13001)).Reason);
        Assert.Equal("window-layout-unavailable", Select(login, behind with { Cloaked = true }).Reason);
        Assert.Equal("window-layout-unavailable", Select(login with { Visible = false }, behind).Reason);
    }

    [Theory]
    [InlineData(20, 0, "login-owned-by-background")]
    [InlineData(0, 10, "background-owned-by-login")]
    [InlineData(0, 0, "unowned")]
    [InlineData(20, 10, null)]
    [InlineData(40, 0, null)]
    [InlineData(10, 0, null)]
    [InlineData(0, 50, null)]
    public void Ownership_must_be_direct_or_both_unowned(long loginOwner, long backdropOwner, string expected) =>
        Assert.Equal(expected, (string)Invoke("GetOwnership", 10L, loginOwner, 20L, backdropOwner));

    [Fact]
    public void Review_hash_keeps_relative_geometry_and_executable_version_but_not_runtime_handles()
    {
        object Root(long id, long owner, bool enabled, int x, int y, int dpi = 96, string shape = null) =>
            Activator.CreateInstance(LayoutWindow, id, owner, WindowClass, true, enabled, dpi,
                new Rectangle(x, y, 300, 240), shape ?? Shape);
        string Hash(object login, object backdrop, string relation = "login-owned-by-background", string version = "1.2.3.4",
            string executableHash = null) => (string)Invoke("LayoutHash", executableHash ?? Shape, version, relation, login, backdrop);

        var first = Root(10, 20, true, 30, 40);
        var behind = Root(20, 0, false, 10, 15);
        string original = Hash(first, behind);
        Assert.True(EvidenceProtocol.IsHash(original));
        Assert.Equal(original, Hash(Root(31, 42, true, 130, 140), Root(42, 0, false, 110, 115)));
        Assert.NotEqual(original, Hash(Root(10, 20, true, 31, 40), behind));
        Assert.NotEqual(original, Hash(first, Root(20, 0, false, 10, 15, dpi: 120)));
        Assert.NotEqual(original, Hash(first, Root(20, 0, false, 10, 15, shape: new string('b', 64))));
        Assert.NotEqual(original, Hash(first, behind, relation: "unowned"));
        Assert.NotEqual(original, Hash(first, behind, version: "1.2.3.5"));
        Assert.NotEqual(original, Hash(first, behind, executableHash: new string('b', 64)));
    }

    [Theory]
    [InlineData("stable", "disabled")]
    [InlineData("revision-changed-before-confirmation", null)]
    [InlineData("native-state-changed", null)]
    [InlineData("graph-changed-after-confirmation", null)]
    public void Collection_requires_the_same_observation_and_unchanged_graph(string scenario, string expected)
    {
        var original = new WindowGraphSnapshot(1, false, new[] { Window(10, "root"), Window(20, "unowned") });
        int snapshotCount = 0;
        int observationCount = 0;
        WindowGraphSnapshot Snapshot() => ++snapshotCount switch
        {
            1 when scenario == "revision-changed-before-confirmation" => original with { Revision = 2 },
            2 when scenario == "graph-changed-after-confirmation" => original with { Truncated = true },
            _ => original
        };
        string Observe(WindowGraphSnapshot graph) => ++observationCount == 2 && scenario == "native-state-changed"
            ? "enabled" : "disabled";

        var method = Backdrop.GetMethod("ObserveStable", BindingFlags.NonPublic | BindingFlags.Static)
            .MakeGenericMethod(typeof(string));
        Assert.Equal(expected, method.Invoke(null, new object[] { original, (Func<WindowGraphSnapshot>)Snapshot,
            (Func<WindowGraphSnapshot, string>)Observe }));
    }
}
