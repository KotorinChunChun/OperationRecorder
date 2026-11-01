using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OperationRecorderApp.Replay;
using Xunit;

namespace OperationRecorderApp.Tests;

// 0.1.6で追加。今回の依頼ではコンパイルのみ、実行しない。
public class ReplayLearningTests
{
    static ReplayCondition Window() => new() { All = new()
    {
        new() { Field = "title", Match = "regex", Value = "請求書 [0-9]{3} - Editor" },
        new() { Field = "className", Match = "regex", Value = "Editor_[a-f0-9]{6}" },
    } };
    [Fact]
    public void 変動箇所の文字種と桁数と論理条件で対象を絞る()
    {
        var condition = Window(); Assert.Empty(condition.Validate(true));
        Assert.True(condition.Matches(ReplayCondition.Window("請求書 123 - Editor", "Editor_abc123")));
        Assert.False(condition.Matches(ReplayCondition.Window("請求書 xx - Editor", "Editor_abc123")));
        condition.Any.Add(new() { Field = "title", Match = "contains", Value = "請求書" });
        condition.None.Add(new() { Field = "title", Match = "contains", Value = "999" });
        Assert.False(condition.Matches(ReplayCondition.Window("請求書 999 - Editor", "Editor_abc123")));
    }
    [Theory]
    [InlineData("(?=Editor).*", "className")]
    [InlineData(".*", "title")]
    [InlineData("Editor_[0-9]+", "executable")]
    public void 不正な正規表現や範囲外の条件を入力前に拒否する(string value, string field) =>
        Assert.NotEmpty(new ReplayCondition { All = new() { new() { Field = field, Match = "regex", Value = value } } }.Validate(true));
    [Fact]
    public void 共有資料から入力値と原本パスとコマンドラインを除外する()
    {
        using var fixture = new Fixture(); fixture.Prepare();
        string shared = File.ReadAllText(Path.Combine(fixture.Bundle, "analysis", "observations.json"));
        Assert.DoesNotContain("DO-NOT-SEND", shared); Assert.DoesNotContain("commandLine", shared);
        Assert.DoesNotContain(fixture.Sources[0], shared); Assert.DoesNotContain("keyCode", shared);
    }
    [Fact]
    public void 原本と操作と入力値を保持し受入後の編集には再確認が必要になる()
    {
        using var fixture = new Fixture(); fixture.Prepare();
        string before = ReplayImporter.SourceHash(fixture.Sources[0]); var config = fixture.Apply();
        Assert.Contains(config.Validate(), s => s.Contains("未確認")); Assert.Empty(config.LearningReview.Unresolved);
        Assert.Equal(new[] { "click", "input" }, config.Steps.Select(s => s.Kind));
        Assert.Equal("DO-NOT-SEND", config.Steps[1].Text.Literal); Assert.Equal(2, config.Sources.Count);
        Assert.Equal(before, ReplayImporter.SourceHash(fixture.Sources[0]));
        string accepted = Path.Combine(fixture.Root, "accepted.json"); ReplayLearning.Accept(fixture.Draft, accepted);
        var confirmed = ReplayConfig.Load(accepted); Assert.Empty(confirmed.Validate());
        confirmed.TimeoutMs++; Assert.Contains(confirmed.Validate(), s => s.Contains("確認後に変更"));
    }
    [Fact]
    public void 複数根拠の欠落と原本変更を確認済みとして扱わない()
    {
        using var fixture = new Fixture(); fixture.Prepare(); var proposal = fixture.Proposal();
        proposal.Steps[0].Correspondences.RemoveAt(1); fixture.WriteProposal(proposal);
        var config = fixture.Apply(false); Assert.NotEmpty(config.LearningReview.Unresolved);
        Assert.Throws<ArgumentException>(() => ReplayLearning.Accept(fixture.Draft, Path.Combine(fixture.Root, "accepted.json")));
        File.AppendAllText(Path.Combine(fixture.Sources[0], "events.jsonl"), "\n");
        Assert.Contains(config.Validate(), e => e.Contains("記録が変更"));
    }
    sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "OpRecLearning-" + Guid.NewGuid().ToString("N"));
        public string[] Sources { get; }
        public string Bundle => Path.Combine(Root, "bundle");
        public string Draft => Path.Combine(Root, "draft.json");
        string ProposalFile => Path.Combine(Root, "proposal.json");
        public Fixture()
        {
            Sources = new[] { Path.Combine(Root, "one"), Path.Combine(Root, "two") };
            for (int index = 0; index < Sources.Length; index++)
            {
                Directory.CreateDirectory(Sources[index]); File.WriteAllText(Path.Combine(Sources[index], "session.json"), "{}");
                int number = index == 0 ? 12 : 34;
                var events = Enumerable.Range(1, 2).Select(seq => JsonSerializer.Serialize(new
                {
                    seq, tMs = seq * 100, kind = seq == 1 ? "click" : "input", keyCode = 65,
                    window = new { title = "請求書 " + (index == 0 ? "123" : "456") + " - Editor", className = "Editor_" + (index == 0 ? "abc123" : "def456"),
                        program = new { path = Environment.ProcessPath, commandLine = "DO-NOT-SEND" }, frame = new { x = 0, y = 0, w = 600, h = 400 } },
                    pointInWindow = new { x = 50, y = 10 },
                    uia = new { automationId = (seq == 1 ? "button_" : "input_") + number, name = seq == 1 ? "保存" : "入力欄", className = "Control_" + number,
                        controlType = seq == 1 ? "Button" : "Edit", boundsInWindow = new { x = 0, y = 0, w = 100, h = 20 } },
                    input = new { value = "DO-NOT-SEND", isPassword = false },
                }));
                File.WriteAllLines(Path.Combine(Sources[index], "events.jsonl"), events);
            }
        }
        public void Prepare()
        {
            string input = Path.Combine(Root, "request.json");
            File.WriteAllText(input, JsonSerializer.Serialize(new ReplayLearningRequest { Sources = Sources.ToList() }, ReplayConfig.JsonOptions));
            ReplayLearning.Prepare(input, Bundle);
        }
        public ReplayLearningProposal Proposal()
        {
            var manifest = ReplayLearning.Read<ReplayLearningManifest>(Path.Combine(Bundle, "manifest.json"));
            return new() { BundleId = manifest.BundleId, ObservationsSha256 = manifest.ObservationsSha256, Summary = "番号と生成IDを比較",
                Steps = Enumerable.Range(1, 2).Select(seq => new ReplayLearningStep
                {
                    Step = seq, WindowCondition = Window(), Reason = "2回の記録で固定部分と文字種が一致",
                    Correspondences = new() { new() { RecordingId = "recording1", SourceSeq = seq }, new() { RecordingId = "recording2", SourceSeq = seq } },
                    ElementCondition = new() { All = new() { new() { Field = "automationId", Match = "regex", Value = (seq == 1 ? "button_" : "input_") + "[0-9]{2}" } } },
                }).ToList() };
        }
        public void WriteProposal(ReplayLearningProposal proposal) => File.WriteAllText(ProposalFile, JsonSerializer.Serialize(proposal, ReplayConfig.JsonOptions));
        public ReplayConfig Apply(bool createProposal = true)
        {
            if (createProposal) WriteProposal(Proposal());
            return ReplayLearning.Apply(Bundle, ProposalFile, Draft);
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
