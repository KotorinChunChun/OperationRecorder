using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using OpRec;
using Xunit;

namespace OperationRecorderApp.Tests
{
    public sealed class CompositionAutomationTests
    {
        static JsonArray Operations(string json) => JsonNode.Parse(json).AsArray();
        static JsonObject Request(CompositionAutomationFixture fixture, JsonArray operations)
        {
            var sources = new JsonArray(); foreach (var source in fixture.Sources) sources.Add(Path.GetRelativePath(fixture.Root, source));
            return new JsonObject { ["schemaVersion"] = 1, ["sources"] = sources, ["operations"] = operations };
        }

        [Fact, Trait("CaseId", "COMP-CLI-001")]
        public void JSONだけで三記録の編集保存完成と既存出力を実行できる()
        {
            using var fixture = new CompositionAutomationFixture(); var before = fixture.InputHashes();
            var request = Request(fixture, Operations("""
              [
                {"type":"select","row":0}, {"type":"choose","candidate":0},
                {"type":"source","source":2,"steps":[1,2]}, {"type":"select","row":2},
                {"type":"insert","position":"before"}, {"type":"move","rows":[2,3],"direction":"first"},
                {"type":"include","rows":[0,1],"included":false}, {"type":"undo"}, {"type":"redo"},
                {"type":"note","headline":"不足した説明","body":"補完する本文"},
                {"type":"text","caption":"CLIで完成","description":"合成の説明","headline":"編集した説明","body":"再開可能な補足"},
                {"type":"save"}, {"type":"inspect"}, {"type":"complete"}
              ]
              """));
            var result = CompositionAutomation.Execute(request, fixture.Workspace);
            Assert.Equal("composition", (string)result["domain"]);
            var state = result["state"].AsObject(); Assert.Equal("CLIで完成", (string)state["caption"]); Assert.False((bool)state["dirty"]);
            Assert.Equal(3, state["sources"].AsArray().Count); Assert.Equal(7, state["rows"].AsArray().Count);
            Assert.Equal(5, state["rows"].AsArray().Count(row => (bool)row["included"]));
            Assert.Empty(state["rows"][2]["candidates"].AsArray()); // 追加した文章行が3行目。
            Assert.Equal("編集した説明", (string)state["rows"][2]["headline"]);
            Assert.True((bool)result["operations"][7]["accepted"]); Assert.True((bool)result["operations"][8]["accepted"]);
            string complete = (string)result["completed"];
            Assert.StartsWith(fixture.Workspace.EnsureDirectory("records") + Path.DirectorySeparatorChar, complete);
            var steps = ReportGenerator.ListSteps(complete); Assert.Equal(5, steps.Count);
            Assert.Contains(steps, step => step.Headline == "編集した説明" && step.Body == "再開可能な補足");
            var options = new ExportCommand.Options { SessionDir = complete, OutDir = fixture.Workspace.EnsureDirectory("exports") };
            var exports = ExportCommand.Run(options);
            Assert.Equal(2, exports.Count); Assert.All(exports, file => Assert.True(File.Exists(file)));
            Assert.Contains("編集した説明", File.ReadAllText(exports.First(file => file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))));
            Assert.Equal(before.OrderBy(pair => pair.Key), fixture.InputHashes().OrderBy(pair => pair.Key));
        }

        [Fact, Trait("CaseId", "COMP-CLI-002")]
        public void 保存作業を別シナリオで再開し閉じる選択を機械判定できる()
        {
            using var fixture = new CompositionAutomationFixture(1);
            var created = CompositionAutomation.Execute(Request(fixture, Operations("""
              [{"type":"text","caption":"保存済みの名前"},{"type":"save"}]
              """)), fixture.Workspace);
            string draft = (string)created["draft"];
            Directory.Move(fixture.Sources[0], fixture.Sources[0] + "-moved");
            var reopened = CompositionAutomation.Execute(new JsonObject { ["schemaVersion"] = 1, ["draft"] = draft, ["operations"] = Operations("""
              [{"type":"text","headline":"保存しない編集"},{"type":"close","decision":"cancel"},{"type":"close","decision":"discard"}]
              """) }, fixture.Workspace);
            Assert.False((bool)reopened["operations"][1]["accepted"]); Assert.True((bool)reopened["operations"][2]["accepted"]);
            Assert.True((bool)reopened["state"]["dirty"]);
            var saved = CompositionProject.Open(draft); Assert.Equal("保存済みの名前", saved.Caption);
            Assert.DoesNotContain(saved.Rows, row => saved.GetStep(row).Headline == "保存しない編集");
        }

        [Theory, Trait("CaseId", "COMP-CLI-003")]
        [InlineData("[{\"type\":\"unknown\"}]")]
        [InlineData("[{\"type\":\"include\",\"incldued\":false}]")]
        [InlineData("[{\"type\":\"choose\",\"candidate\":-1}]")]
        [InlineData("[{\"type\":\"move\",\"direction\":\"sideways\"}]")]
        [InlineData("[{\"type\":\"source\",\"source\":0}]")]
        [InlineData("[{\"type\":\"select\",\"rows\":[0],\"row\":0}]")]
        [InlineData("[{\"type\":\"insert\",\"index\":0,\"position\":\"end\"}]")]
        [InlineData("[{\"type\":\"move\",\"index\":0,\"direction\":\"up\"}]")]
        public void 不正なシナリオは作業フォルダーを作る前に拒否する(string operations)
        {
            using var fixture = new CompositionAutomationFixture(1); var before = fixture.InputHashes();
            Assert.Throws<ArgumentException>(() => CompositionAutomation.Execute(Request(fixture, Operations(operations)), fixture.Workspace));
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, "compositions")));
            Assert.Equal(before.OrderBy(pair => pair.Key), fixture.InputHashes().OrderBy(pair => pair.Key));
        }

        [Fact, Trait("CaseId", "COMP-CLI-004")]
        public void 領域外の元記録や途中保存は拒否する()
        {
            using var fixture = new CompositionAutomationFixture(1); using var outside = new TestSession();
            var request = Request(fixture, new JsonArray()); request["sources"] = new JsonArray(outside.Dir);
            Assert.Throws<ArgumentException>(() => CompositionAutomation.Execute(request, fixture.Workspace));
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, "compositions")));
            Assert.Throws<ArgumentException>(() => CompositionAutomation.Execute(new JsonObject { ["schemaVersion"] = 1, ["draft"] = "../draft.json" }, fixture.Workspace));
        }

        [Fact, Trait("CaseId", "COMP-CLI-005")]
        public void 無効な操作で最後に保存した作業を変更しない()
        {
            using var fixture = new CompositionAutomationFixture(1);
            var created = CompositionAutomation.Execute(Request(fixture, new JsonArray()), fixture.Workspace);
            string draft = (string)created["draft"]; string saved = File.ReadAllText(draft);
            var request = new JsonObject { ["schemaVersion"] = 1, ["draft"] = draft, ["operations"] = Operations("""
              [{"type":"text","headline":"未保存の編集"},{"type":"move","rows":[1],"index":999}]
              """) };
            Assert.Throws<ArgumentOutOfRangeException>(() => CompositionAutomation.Execute(request, fixture.Workspace));
            Assert.Equal(saved, File.ReadAllText(draft)); Assert.False(CompositionEditingSession.Open(draft).IsDirty);
        }

        [Fact, Trait("CaseId", "COMP-CLI-006")]
        public void 推定なし追加と直接位置指定と採用復帰をシナリオ実行する()
        {
            using var fixture = new CompositionAutomationFixture(2);
            var request = Request(fixture, Operations("""
              [{"type":"add-source","path":"inputs/source-1","suggest":false},
               {"type":"insert","source":1,"steps":[3,4],"index":0},
               {"type":"exclude","rows":[0,1]},{"type":"include","rows":[0,1]},
               {"type":"move","rows":[0,1],"index":6},{"type":"commit"},{"type":"save"}]
              """));
            request["sources"] = new JsonArray("inputs/source-0");
            var result = CompositionAutomation.Execute(request, fixture.Workspace);
            Assert.Equal(2, result["state"]["sources"].AsArray().Count); Assert.Equal(6, result["state"]["rows"].AsArray().Count);
            Assert.Equal(3, (long)result["state"]["rows"][4]["candidates"][0]["seq"]);
            Assert.Equal(4, (long)result["state"]["rows"][5]["candidates"][0]["seq"]);
            Assert.All(result["state"]["rows"].AsArray(), row => Assert.True((bool)row["included"]));
        }

        [Theory, Trait("CaseId", "COMP-CLI-007")]
        [InlineData("[{\"type\":\"insert\",\"index\":0}]")]
        [InlineData("[{\"type\":\"insert\",\"source\":0,\"steps\":[],\"index\":0}]")]
        [InlineData("[{\"type\":\"select\",\"rows\":[]},{\"type\":\"exclude\"}]")]
        public void 対象の未選択を暗黙の成功にせず最後の保存を残す(string operations)
        {
            using var fixture = new CompositionAutomationFixture(1);
            var created = CompositionAutomation.Execute(Request(fixture, new JsonArray()), fixture.Workspace);
            string draft = (string)created["draft"], previous = File.ReadAllText(draft);
            Assert.Throws<ArgumentException>(() => CompositionAutomation.Execute(new JsonObject
            {
                ["schemaVersion"] = 1, ["draft"] = draft, ["operations"] = Operations(operations),
            }, fixture.Workspace));
            Assert.Equal(previous, File.ReadAllText(draft));
        }

        [Fact, Trait("CaseId", "COMP-EXE-001")]
        public void 実EXEで三記録から新しい記録を完成し再開と失敗を終了コードで判定する()
        {
            using var scope = new AutomationScope();
            var sources = new[] { scope.Session("a"), scope.Session("b"), scope.Session("c") };
            var originals = Directory.GetFiles(scope.Workspace.Resolve("sessions"), "*", SearchOption.AllDirectories)
                .ToDictionary(file => file, file => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))));
            var execution = scope.Run("composition", new JsonObject
            {
                ["sources"] = new JsonArray(sources.Select(source => (JsonNode)JsonValue.Create(source)).ToArray()),
                ["operations"] = Operations("""
                  [{"type":"select","row":0},{"type":"choose","candidate":0},
                   {"type":"text","caption":"実EXEで合成した記録","description":"3本の元記録を保持"},
                   {"type":"source","source":2,"steps":[3,4]},{"type":"select","row":1},
                   {"type":"insert","position":"before"},{"type":"move","rows":[1,2],"direction":"last"},
                   {"type":"exclude","row":0},{"type":"select","row":2},
                   {"type":"note","headline":"不足した確認","body":"CLIから補完した説明"},
                   {"type":"save"},{"type":"complete"}]
                  """),
            });
            Assert.Equal(0, execution.Code); Assert.True((bool)execution.Result["success"]);
            var result = execution.Result["result"];
            Assert.Equal("実EXEで合成した記録", (string)result["state"]["caption"]);
            Assert.Equal(7, result["state"]["rows"].AsArray().Count); Assert.False((bool)result["state"]["dirty"]);
            var complete = (string)result["completed"]; var steps = ReportGenerator.ListSteps(complete);
            Assert.Equal(6, steps.Count); Assert.Equal(Enumerable.Range(1, 6).Select(number => (long)number), steps.Select(step => step.Seq));
            Assert.Contains(steps, step => step.Headline == "不足した確認" && step.Body == "CLIから補完した説明");
            var draft = (string)result["draft"]; var saved = File.ReadAllText(draft);
            var resume = scope.Run("composition", new JsonObject { ["draft"] = draft, ["operations"] = Operations("[{\"type\":\"inspect\"}]") });
            Assert.Equal(0, resume.Code); Assert.Equal(7, resume.Result["result"]["state"]["rows"].AsArray().Count);
            Assert.False((bool)resume.Result["result"]["state"]["canUndo"]);
            var invalid = scope.Run("composition", new JsonObject
            {
                ["draft"] = draft, ["operations"] = Operations("[{\"type\":\"move\",\"row\":1,\"index\":999}]"),
            });
            Assert.Equal(2, invalid.Code); Assert.False((bool)invalid.Result["success"]); Assert.Equal(saved, File.ReadAllText(draft));
            Assert.All(originals, file => Assert.Equal(file.Value, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file.Key)))));
        }
    }
}
