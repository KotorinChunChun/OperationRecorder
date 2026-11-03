using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using OpRec;
using Xunit;

namespace OperationRecorderApp.Tests
{
    public sealed class CompositionEngineTests
    {
        sealed class Workspace : IDisposable
        {
            public string Root { get; } = Path.Combine(Path.GetTempPath(), "oprec-composition-" + Guid.NewGuid().ToString("N"));
            public string Drafts => Path.Combine(Root, ".compositions");
            public string Output => Path.Combine(Root, "completed");
            public Workspace() { Directory.CreateDirectory(Root); }
            public void Dispose() { Directory.Delete(Root, true); }
        }

        static void Steps(TestSession session, params string[] ids)
        {
            var original = SessionMerger.LoadEvents(session.Dir)[0];
            var events = new List<string>();
            for (var index = 0; index < ids.Length; index++)
            {
                var value = original.DeepClone().AsObject();
                value["seq"] = index + 1;
                value["tMs"] = (index + 1) * 1000L;
                value["uia"]["automationId"] = ids[index];
                value["uia"]["name"] = ids[index];
                events.Add(value.ToJsonString());
            }
            File.WriteAllLines(Path.Combine(session.Dir, "events.jsonl"), events, new UTF8Encoding(false));
        }

        static Dictionary<string, string> Hashes(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(directory, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        static string Id(CompositionProject project, CompositionRow row) => (string)SessionMerger.LoadEvents(
            Path.Combine(project.DirectoryPath, "sources", row.Candidates[row.SelectedCandidate].SourceId))
                .Single(value => (long)value["seq"] == row.Candidates[row.SelectedCandidate].Seq)["uia"]["automationId"];

        [Fact]
        public void 三つの記録を突き合わせて候補と除外を保持する()
        {
            using var a = new TestSession(); using var b = new TestSession(); using var c = new TestSession(); using var work = new Workspace();
            Steps(a, "start", "common", "wrong"); Steps(b, "common", "middle", "end"); Steps(c, "middle", "end", "finish");
            var project = CompositionProject.Create(new[] { a.Dir, b.Dir, c.Dir }, work.Drafts);
            Assert.Equal(3, project.Sources.Count);
            Assert.Equal(new[] { "start", "common", "middle", "end", "finish" }, project.Rows.Where(row => row.Included).Select(row => Id(project, row)));
            Assert.False(project.Rows.Single(row => Id(project, row) == "wrong").Included);
            var common = project.Rows.Single(row => Id(project, row) == "common");
            Assert.Equal(2, common.Candidates.Count);
            Assert.Equal(project.Sources[1].Id, common.Candidates[common.SelectedCandidate].SourceId);
            project.Choose(common.Id, 0);
            Assert.Equal(project.Sources[0].Id, common.Candidates[common.SelectedCandidate].SourceId);
        }

        [Fact]
        public void 追加読込で既存の採否候補選択文章と相対順を変更しない()
        {
            using var a = new TestSession(); using var b = new TestSession(); using var work = new Workspace();
            Steps(a, "one", "two", "three"); Steps(b, "two", "four");
            var project = CompositionProject.Create(new[] { a.Dir }, work.Drafts);
            var originalIds = project.Rows.Select(row => row.Id).ToArray();
            project.Rows[1].Included = false;
            project.Rows[1].HeadlineOverride = "利用者が直した文章";
            project.AddSource(b.Dir);
            Assert.Equal(originalIds, project.Rows.Where(row => originalIds.Contains(row.Id)).Select(row => row.Id));
            var row = project.Rows.Single(value => value.Id == originalIds[1]);
            Assert.False(row.Included); Assert.Equal(0, row.SelectedCandidate); Assert.Equal(2, row.Candidates.Count);
            Assert.Equal("利用者が直した文章", project.GetStep(row).Headline);
            Assert.Contains(project.Rows, value => Id(project, value) == "four");
        }

        [Fact]
        public void 範囲差し込みと移動除外文章を保存再開し完成できる()
        {
            using var a = new TestSession(marksOutput: true); using var b = new TestSession(); using var work = new Workspace();
            Steps(a, "one", "two", "three"); Steps(b, "missing1", "missing2");
            ReportGenerator.SaveStepEdit(b.Dir, 2, "補完元の編集済み見出し", "補完元の本文");
            var beforeA = Hashes(a.Dir); var beforeB = Hashes(b.Dir);
            var project = CompositionProject.Create(new[] { a.Dir }, work.Drafts);
            project.AddSource(b.Dir, false);
            project.Insert(project.Sources[1].Id, new long[] { 1, 2 }, 1);
            var moved = project.Rows.TakeLast(1).Single().Id;
            project.Move(new[] { moved }, 1);
            project.Rows[0].Included = false;
            project.AddNote("補足説明", "操作の前提を確認します。", 1);
            project.Caption = "完成手順";
            project.Description = "合成作業の説明";
            project.Save();
            var state = project.CaptureState();
            var reopened = CompositionProject.Open(project.DraftFile);
            Assert.Equal(state, reopened.CaptureState());
            var completed = reopened.Complete(work.Output);
            var steps = ReportGenerator.ListSteps(completed);
            Assert.Equal(5, steps.Count);
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, steps.Select(step => step.Number));
            Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, steps.Select(step => step.Seq));
            Assert.Equal("補足説明", steps[0].Headline); Assert.Null(steps[0].BeforePath);
            Assert.Contains(steps, step => step.Headline == "補完元の編集済み見出し" && step.Body == "補完元の本文");
            Assert.All(steps.Where(step => step.BeforePath != null), step => Assert.True(File.Exists(step.BeforePath) && File.Exists(step.AfterPath)));
            var completedEvents = SessionMerger.LoadEvents(completed);
            Assert.All(completedEvents.Where(value => (string)value["mergedFrom"] == project.Sources[1].Id), value => Assert.True((bool)value["marksBaked"]));
            Assert.All(completedEvents, value => Assert.NotNull(value["compositionOrigin"]));
            Assert.Equal(beforeA.OrderBy(pair => pair.Key), Hashes(a.Dir).OrderBy(pair => pair.Key));
            Assert.Equal(beforeB.OrderBy(pair => pair.Key), Hashes(b.Dir).OrderBy(pair => pair.Key));
            Assert.Equal("完成手順", (string)JsonNode.Parse(File.ReadAllText(Path.Combine(completed, "session.json")))["caption"]);
        }

        [Fact]
        public void 元記録を移動した後も画像と編集情報から再開して出力できる()
        {
            using var a = new TestSession(); using var work = new Workspace();
            ReportGenerator.SaveStepEdit(a.Dir, 1, "移動前に修正", "本文");
            var project = CompositionProject.Create(new[] { a.Dir }, work.Drafts);
            Directory.Move(a.Dir, a.Dir + "-moved");
            var reopened = CompositionProject.Open(project.DraftFile);
            var completed = reopened.Complete(work.Output);
            var steps = ReportGenerator.ListSteps(completed);
            Assert.Equal("移動前に修正", steps[0].Headline);
            Assert.True(File.Exists(steps[0].BeforePath));
            var generated = new List<string>();
            OperationRecorderApp.RecordingController.Export(Path.Combine(AppContext.BaseDirectory, "templates"), completed, new[] { "user" }, null, false, true, false, null, null, generated, new List<string>());
            Assert.Contains("移動前に修正", File.ReadAllText(generated.Single()));
        }

        [Fact]
        public void 文章のみの記録を完成でき除外は復帰できる()
        {
            using var a = new TestSession(); using var work = new Workspace();
            var project = CompositionProject.Create(new[] { a.Dir }, work.Drafts);
            foreach (var row in project.Rows) row.Included = false;
            Assert.Throws<IOException>(() => project.Complete(work.Output));
            project.Rows[0].Included = true;
            project.AddNote("前提", "テスト用の説明です。", 0);
            var completed = project.Complete(work.Output);
            Assert.Equal(2, ReportGenerator.ListSteps(completed).Count);
        }

        [Fact]
        public void Undo用状態復元は追加元と自由編集を往復できる()
        {
            using var a = new TestSession(); using var b = new TestSession(); using var work = new Workspace();
            var project = CompositionProject.Create(new[] { a.Dir }, work.Drafts);
            var before = project.CaptureState();
            project.AddSource(b.Dir); project.AddNote("追記", "説明", 1);
            var after = project.CaptureState();
            project.RestoreState(before); Assert.Single(project.Sources); Assert.Equal(4, project.Rows.Count);
            project.RestoreState(after); Assert.Equal(2, project.Sources.Count); Assert.Equal(5, project.Rows.Count);
        }

        [Fact]
        public void 不正な作業参照を拒否し現在の編集と保存済みJSONを保持する()
        {
            using var a = new TestSession(); using var work = new Workspace();
            var project = CompositionProject.Create(new[] { a.Dir }, work.Drafts);
            var before = project.CaptureState(); var fileBefore = File.ReadAllText(project.DraftFile);
            var json = JsonNode.Parse(before);
            json["Rows"][0]["Candidates"][0]["Seq"] = 999999;
            Assert.Throws<IOException>(() => project.RestoreState(json.ToJsonString()));
            Assert.Equal(before, project.CaptureState()); Assert.Equal(fileBefore, File.ReadAllText(project.DraftFile));
            json = JsonNode.Parse(before); json["Sources"][0]["Id"] = "../outside";
            Assert.Throws<IOException>(() => project.RestoreState(json.ToJsonString()));
            json = JsonNode.Parse(before); json["SchemaVersion"] = 999;
            Assert.Throws<IOException>(() => project.RestoreState(json.ToJsonString()));
        }

        [Fact]
        public void 保存先がロック中なら前回の作業ファイルを保持する()
        {
            using var a = new TestSession(); using var work = new Workspace();
            var project = CompositionProject.Create(new[] { a.Dir }, work.Drafts);
            var before = File.ReadAllText(project.DraftFile);
            project.Caption = "未保存の名前";
            using (var locked = new FileStream(project.DraftFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.ThrowsAny<IOException>(() => project.Save());
            Assert.Equal(before, File.ReadAllText(project.DraftFile));
            Assert.Empty(Directory.GetFiles(project.DirectoryPath, ".draft-*.tmp"));
            project.Save(); Assert.Equal("未保存の名前", CompositionProject.Open(project.DraftFile).Caption);
        }

        [Fact]
        public void 参照画像が欠けた入力は取り込まず作業を残さない()
        {
            using var a = new TestSession(); using var work = new Workspace();
            File.Delete(a.ImagePath(1, "before"));
            Assert.Throws<IOException>(() => CompositionProject.Create(new[] { a.Dir }, work.Drafts));
            Assert.Empty(Directory.GetDirectories(work.Drafts));
        }

        [Theory]
        [InlineData("../outside.png")]
        [InlineData("C:/outside.png")]
        [InlineData("img/../outside.png")]
        public void 記録外の画像参照を拒否する(string path)
        {
            using var a = new TestSession(); using var work = new Workspace();
            var events = SessionMerger.LoadEvents(a.Dir); events[0]["before"]["file"] = path;
            File.WriteAllLines(Path.Combine(a.Dir, "events.jsonl"), events.Select(value => value.ToJsonString()));
            Assert.Throws<IOException>(() => CompositionProject.Create(new[] { a.Dir }, work.Drafts));
        }

        [Fact]
        public void 壊れた画像とJSONを拒否する()
        {
            using var a = new TestSession(); using var work = new Workspace();
            File.WriteAllText(a.ImagePath(1, "before"), "not an image");
            Assert.Throws<IOException>(() => CompositionProject.Create(new[] { a.Dir }, work.Drafts));
            File.WriteAllText(Path.Combine(a.Dir, "session.json"), "{broken");
            Assert.Throws<IOException>(() => CompositionProject.Create(new[] { a.Dir }, work.Drafts));
        }

        [Fact]
        public void 保存済み画像の欠落と変更を検出して不完全な完成記録を残さない()
        {
            using var a = new TestSession(); using var work = new Workspace();
            var project = CompositionProject.Create(new[] { a.Dir }, work.Drafts);
            var image = project.SourceSteps(project.Sources[0].Id)[0].BeforePath;
            File.Delete(image);
            Assert.Throws<IOException>(() => project.Complete(work.Output));
            Assert.False(Directory.Exists(work.Output));
            Assert.Throws<IOException>(() => CompositionProject.Open(project.DraftFile));
        }

        [Fact]
        public void 不正な追加と移動は既存作業に影響しない()
        {
            using var a = new TestSession(); using var b = new TestSession(); using var work = new Workspace();
            var project = CompositionProject.Create(new[] { a.Dir }, work.Drafts);
            var before = project.CaptureState();
            Assert.Throws<IOException>(() => project.AddSource(a.Dir));
            File.Delete(b.ImagePath(1, "before"));
            Assert.Throws<IOException>(() => project.AddSource(b.Dir));
            Assert.Throws<IOException>(() => project.Insert(project.Sources[0].Id, new long[] { 999 }, 0));
            Assert.Throws<IOException>(() => project.Move(new[] { "missing-row" }, 0));
            Assert.Equal(before, project.CaptureState());
            Assert.Throws<IOException>(() => project.Complete(a.Dir));
        }

        [Fact]
        public void 複数行移動は元の相対順を維持する()
        {
            using var a = new TestSession(); using var work = new Workspace();
            var project = CompositionProject.Create(new[] { a.Dir }, work.Drafts);
            var original = project.Rows.Select(row => row.Id).ToArray();
            project.Move(new[] { original[0], original[2] }, 4);
            Assert.Equal(new[] { original[1], original[3], original[0], original[2] }, project.Rows.Select(row => row.Id));
        }

        [Fact]
        public void 完成記録の期間は全元記録を含み合成日時と区別する()
        {
            using var a = new TestSession(); using var b = new TestSession(); using var work = new Workspace();
            var session = JsonNode.Parse(File.ReadAllText(Path.Combine(b.Dir, "session.json")));
            session["start"] = "2026-10-20T08:00:00+09:00";
            session["end"] = "2026-10-23T18:00:00+09:00";
            File.WriteAllText(Path.Combine(b.Dir, "session.json"), session.ToJsonString());
            var project = CompositionProject.Create(new[] { a.Dir, b.Dir }, work.Drafts);
            var completed = JsonNode.Parse(File.ReadAllText(Path.Combine(project.Complete(work.Output), "session.json")));
            Assert.Equal("2026-10-20T08:00:00+09:00", (string)completed["start"]);
            Assert.Equal("2026-10-23T18:00:00+09:00", (string)completed["end"]);
            Assert.True(DateTimeOffset.TryParse((string)completed["composedAt"], out _));
            Assert.Throws<IOException>(() => project.AddSource(a.Dir + Path.DirectorySeparatorChar));
        }

        [Fact]
        public async Task 複数記録の画像を同時生成してもPNGとして保存できる()
        {
            const int workers = 16;
            using var ready = new CountdownEvent(workers);
            using var start = new ManualResetEventSlim();
            var tasks = Enumerable.Range(0, workers).Select(_ => Task.Factory.StartNew(() =>
            {
                ready.Signal(); start.Wait();
                using var session = new TestSession();
                var files = Directory.GetFiles(session.Dir, "*.png", SearchOption.AllDirectories);
                Assert.Equal(8, files.Length);
                foreach (var file in files)
                {
                    using var image = System.Drawing.Image.FromFile(file);
                    Assert.Equal(320, image.Width); Assert.Equal(200, image.Height);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            ready.Wait(); start.Set();
            await Task.WhenAll(tasks);
        }
    }
}
