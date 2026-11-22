using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

public sealed class WindowGraphRevisionSourceTests
{
    const uint LocationChange = 0x800B, StateChange = 0x800A;

    [Fact]
    public void WinEventは比較境界の間だけを種類とウィンドウの役割で集計する()
    {
        const int pid = 100;
        var events = new WindowGraphWinEvents((hwnd, _) => hwnd switch
        {
            10 or 11 => 10, 20 or 21 => 20, 30 => 30, _ => 0
        });
        var epoch = new object();
        events.Record(LocationChange, 10, 10, pid, false);
        var first = events.Capture(initialBackground: 20);
        events.Record(LocationChange, 10, 10, pid, false);
        events.Record(LocationChange, 10, 10, pid, false);
        events.Record(LocationChange, 10, 20, pid, false);
        events.Record(LocationChange, 10, 11, pid, false);
        events.Record(LocationChange, 10, 21, pid, false);
        events.Record(LocationChange, 10, 30, pid, false);
        events.Record(LocationChange, 10, 40, pid, false);
        events.Record(StateChange, 10, 20, pid, true);
        events.Record(StateChange, 10, 21, pid, false); // The destroyed backdrop cannot be reused as an identity.
        var last = events.Capture(initialBackground: 99);
        events.Record(LocationChange, 10, 10, pid, false);

        var graph = new WindowGraphSnapshot(1, false, Array.Empty<WindowObservation>());
        var initial = graph.ApplyWinEventCount(first.Count, false, epoch, boundary: first);
        var final = graph.ApplyWinEventCount(last.Count, false, epoch, boundary: last);
        int snapshots = 0;
        var (observation, change) = PasswordLayoutBackdrop.ObserveStable(initial,
            () => ++snapshots == 1 ? initial : final, _ => "observed", (_, _) => null);
        Assert.Null(observation);
        Assert.Equal("final-snapshot", change.Stage);
        Assert.Equal(new[] { "win-event" }, change.GraphRevisionSources);
        Assert.Null(change.SnapshotTruncationReasons);
        Assert.Equal("known", change.WinEvents.Status);
        Assert.Equal(9, change.WinEvents.TotalCount);
        Assert.Equal(new[] {
            new WindowGraphEventCount(StateChange, "unknown", 2),
            new WindowGraphEventCount(LocationChange, "background", 1),
            new WindowGraphEventCount(LocationChange, "background-child", 1),
            new WindowGraphEventCount(LocationChange, "other", 1),
            new WindowGraphEventCount(LocationChange, "target", 2),
            new WindowGraphEventCount(LocationChange, "target-child", 1),
            new WindowGraphEventCount(LocationChange, "unknown", 1)
        }, change.WinEvents.Counts);
    }

    [Fact]
    public void 区間が確定できない応答を確定した0件と区別する()
    {
        var events = new WindowGraphWinEvents((_, _) => 0);
        var epoch = new object();
        var cutoff = events.Capture(initialBackground: 20);
        var first = new WindowGraphRevisionSource(epoch, 1, cutoff.Count, EventBoundary: cutoff);
        var noChange = WindowGraphEventInterval.Between(first, first);
        Assert.Equal("known", noChange.Status);
        Assert.Equal(0, noChange.TotalCount);
        Assert.Empty(noChange.Counts);

        var graph = new WindowGraphSnapshot(1, false, Array.Empty<WindowObservation>());
        var initial = graph.ApplyWinEventCount(cutoff.Count + 1, false, epoch);
        var older = graph.ApplyWinEventCount(cutoff.Count, false, epoch, boundary: cutoff);
        var (observation, change) = PasswordLayoutBackdrop.ObserveStable(initial, () => older, _ => "observed", (_, _) => null);
        Assert.Null(observation);
        Assert.Equal("confirmation-snapshot", change.Stage);
        Assert.Equal("unknown", change.WinEvents.Status);
        Assert.Null(change.WinEvents.TotalCount);
        string serialized = JsonSerializer.Serialize(change, EvidenceProtocol.Json);
        using var json = JsonDocument.Parse(serialized);
        var detail = json.RootElement.GetProperty("winEvents");
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("totalCount").ValueKind);
        Assert.False(detail.TryGetProperty("counts", out _));
        Assert.False(json.RootElement.TryGetProperty("snapshotTruncationReasons", out _));

        var missingBuckets = WindowGraphEventInterval.Between(older.RevisionSource, initial.RevisionSource);
        Assert.Equal("counts-unavailable", missingBuckets.Status);
        Assert.Equal(1, missingBuckets.TotalCount);
        Assert.Null(missingBuckets.Counts);
    }

    [Fact]
    public async Task 応答サイズを超える内訳でも確定した総件数と診断理由を返す()
    {
        string[] roles = ["target", "target-child", "background", "background-child", "other", "unknown"];
        var counts = Enumerable.Range(0x8000, 256).SelectMany(eventId => roles
            .Select(role => new WindowGraphEventCount((uint)eventId, role, 12345))).ToArray();
        long total = counts.Sum(entry => entry.Count);
        var change = new PasswordLayoutChange("confirmation-snapshot", ["graph-revision", "snapshot-truncated"],
            ["win-event"], new("known", total, counts), ["concurrent-win-event"]);
        var reply = new PasswordLayoutCandidateReply("diagnostic-only",
            Diagnostics: new("window-layout-changed", Change: change));
        using var pipe = new MemoryStream();
        await PasswordLayoutCandidate.WriteReplyAsync(pipe, reply);
        pipe.Position = 0;
        var result = await EvidenceWire.ReadAsync<PasswordLayoutCandidateReply>(pipe, CancellationToken.None);
        Assert.Equal("diagnostic-only", result.Status);
        Assert.Equal("window-layout-changed", result.Diagnostics.Reason);
        Assert.Equal("counts-unavailable", result.Diagnostics.Change.WinEvents.Status);
        Assert.Equal(total, result.Diagnostics.Change.WinEvents.TotalCount);
        Assert.Null(result.Diagnostics.Change.WinEvents.Counts);
        Assert.Equal(new[] { "concurrent-win-event" }, result.Diagnostics.Change.SnapshotTruncationReasons);
    }

    [Theory]
    [InlineData(false, false, false, true, "unknown")]
    [InlineData(true, false, false, false, "window-limit")]
    [InlineData(false, true, false, false, "window-read-failed")]
    [InlineData(false, false, true, false, "concurrent-win-event")]
    [InlineData(true, true, true, false, "window-limit", "window-read-failed", "concurrent-win-event")]
    public void 不完全になったすべての理由を返す(bool limit, bool missing, bool interrupted, bool unknown, params string[] expected)
    {
        var truncation = limit ? WindowGraphTruncation.WindowLimit : WindowGraphTruncation.None;
        var inputs = WindowGraphRegistry.ReadSnapshotWindows(new long[] { 1, 2 }, handle =>
            missing && handle == 2 ? throw new EvidenceUnavailableException("window-changed") :
                new(handle, 0, 0, 10, false, true, false, false, System.Drawing.Rectangle.Empty, 96, "root"),
            ref truncation);
        Assert.Equal(missing ? 1 : 2, inputs.Count);
        var graph = new WindowGraphTracker().Update(inputs, unknown || truncation != WindowGraphTruncation.None);
        var final = graph.ApplyWinEventCount(7, interrupted, new object(), truncation);
        var first = final with { Truncated = false };
        var (_, change) = PasswordLayoutBackdrop.ObserveStable(first, () => final, _ => "observed", (_, _) => null);
        Assert.Equal(expected, PasswordLayoutBackdrop.TruncationReasons(final));
        Assert.Null(PasswordLayoutBackdrop.TruncationReasons(first));
        Assert.Equal(expected, change.SnapshotTruncationReasons);
        Assert.Equal(new[] { "snapshot-truncated" }, change.Kinds);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void 診断用の改訂内訳を保持しても従来のグラフと同じ形式と判定値になる(
        bool alreadyTruncated, bool interrupted, bool expectedTruncated)
    {
        var windows = System.Array.Empty<WindowObservation>();
        var epoch = new object();
        var tracked = new WindowGraphSnapshot(7, alreadyTruncated, windows);

        var diagnostic = tracked.ApplyWinEventCount(3, interrupted, epoch);

        var source = Assert.IsType<WindowGraphRevisionSource>(diagnostic.RevisionSource);
        Assert.Same(epoch, source.Epoch);
        Assert.Equal(7, source.TrackerRevision);
        Assert.Equal(3, source.WinEventCount);

        var expected = new WindowGraphSnapshot(10, expectedTruncated, windows);
        Assert.Equal(expected, diagnostic);
        Assert.Equal(expected.GetHashCode(), diagnostic.GetHashCode());
        Assert.Equal(expected.ToString(), diagnostic.ToString());
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(diagnostic));
        var (revision, truncated, observedWindows) = diagnostic;
        Assert.Equal(10, revision);
        Assert.Equal(expectedTruncated, truncated);
        Assert.Same(windows, observedWindows);
        Assert.Null(expected.RevisionSource);
        Assert.Null(tracked.ApplyWinEventCount(3, interrupted, revisionEpoch: null).RevisionSource);
    }
}
