using System;
using System.Drawing;
using System.Reflection;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

public sealed class WindowTransitionTests
{
    static readonly EvidenceTarget Source = new(123, DateTimeOffset.UnixEpoch, @"C:\fixture.exe", 1, 10);
    static readonly MethodInfo SelectCandidate = typeof(EvidenceTarget).Assembly
        .GetType("OperationRecorderApp.ExternalEvidence.WindowTransition", throwOnError: true)
        .GetMethod("Select", BindingFlags.Static | BindingFlags.NonPublic);

    static WindowGraphInput Top(long hwnd, bool visible = true, long owner = 0, int pid = 123, bool minimized = false,
        bool cloaked = false) =>
        new(hwnd, 0, owner, pid, false, visible, minimized, cloaked, new Rectangle(10, 10, 100, 100), 96, "unowned");

    static long Select(params WindowGraphInput[] windows) => (long)SelectCandidate.Invoke(null, new object[] { Source, windows });

    [Fact]
    public void Closed_or_hidden_source_allows_one_distinct_root()
    {
        Assert.Equal(20L, Select(Top(20)));
        Assert.Equal(20L, Select(Top(10, visible: false), Top(19, visible: false), Top(20)));
        Assert.Equal(0L, Select(Top(10), Top(20)));
        Assert.Equal(0L, Select(Top(20), Top(21)));
        Assert.Equal(0L, Select(Top(20, pid: 245)));
        Assert.Equal(0L, Select(Top(20, minimized: true)));
        Assert.Equal(0L, Select(Top(20, cloaked: true)));
    }

    [Fact]
    public void One_direct_owned_candidate_may_accompany_the_root()
    {
        Assert.Equal(20L, Select(Top(10, visible: false), Top(20), Top(30, owner: 20)));
        Assert.Equal(0L, Select(Top(10, visible: false), Top(20), Top(30, owner: 10)));
        Assert.Equal(0L, Select(Top(20), Top(30, owner: 20, pid: 245)));
        Assert.Equal(0L, Select(Top(20), Top(30, owner: 20), Top(31, owner: 20)));
        Assert.Equal(0L, Select(Top(20), Top(30, owner: 20, cloaked: true)));
    }
}
