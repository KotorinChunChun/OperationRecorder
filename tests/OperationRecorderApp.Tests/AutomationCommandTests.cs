using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace OperationRecorderApp.Tests
{
    public sealed class AutomationScope : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "oprec-cli-" + Guid.NewGuid().ToString("N"));
        public AutomationWorkspace Workspace { get; }
        public AutomationScope() { Workspace = new AutomationWorkspace(Root); }
        public string Session(string name = "sample")
        {
            using (var sample = new TestSession(marksOutput: true))
            {
                var destination = Workspace.EnsureDirectory("sessions/" + name);
                foreach (var file in Directory.EnumerateFiles(sample.Dir, "*", SearchOption.AllDirectories))
                {
                    var path = Path.Combine(destination, Path.GetRelativePath(sample.Dir, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)); File.Copy(file, path);
                }
                return destination;
            }
        }
        public (int Code, string Output, string Error) Process(params string[] args)
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "OperationRecorder.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using (var process = System.Diagnostics.Process.Start(start))
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(60000)) { process.Kill(true); throw new TimeoutException("自動操作CLIが終了しません。"); }
                Task.WaitAll(stdout, stderr);
                return (process.ExitCode, stdout.Result, stderr.Result);
            }
        }
        public (int Code, JsonObject Result) Run(string area, JsonObject request, string result = null)
        {
            request["schemaVersion"] = 1;
            var input = "requests/" + Guid.NewGuid().ToString("N") + ".json";
            var output = result ?? "results/" + Guid.NewGuid().ToString("N") + ".json";
            Workspace.WriteJson(input, request);
            var executed = Process("--automation", area, "--input", Workspace.Resolve(input), "--workspace", Root, "--result", output);
            var report = JsonNode.Parse(executed.Output.Trim()).AsObject();
            Assert.Equal(executed.Code, report["exitCode"].GetValue<int>());
            if (File.Exists(Workspace.Resolve(output))) Assert.Equal(report.ToJsonString(), JsonNode.Parse(File.ReadAllText(Workspace.Resolve(output))).ToJsonString());
            return (executed.Code, report);
        }
        public void Dispose()
        {
            if (Directory.Exists(Root) && Path.GetFileName(Root).StartsWith("oprec-cli-", StringComparison.Ordinal) &&
                File.Exists(Path.Combine(Root, AutomationWorkspace.MarkerName))) Directory.Delete(Root, true);
        }
    }

    [Collection("管理画面の隔離設定")]
    public class BasicAutomationTests
    {
        [Fact]
        public void 記録の名前文章削除と両形式出力を同じサービス経路で実行する()
        {
            using var scope = new AutomationScope(); var session = scope.Session();
            JsonObject Run(string action, JsonObject data = null)
            {
                data ??= new JsonObject(); data["schemaVersion"] = 1; data["action"] = action; data["session"] = session;
                return BasicAutomation.Execute("session", data, scope.Workspace);
            }
            Assert.Equal(4, ((JsonArray)Run("inspect")["steps"]).Count);
            Assert.Equal("変更した記録", Run("metadata", new JsonObject { ["caption"] = "変更した記録" })["caption"].GetValue<string>());
            Run("edit", new JsonObject { ["seq"] = 2, ["headline"] = "文章を修正", ["body"] = "説明文" });
            Assert.Equal("文章を修正", Run("inspect")["steps"][1]["headline"].GetValue<string>());
            Run("reset", new JsonObject { ["seq"] = 2 });
            Assert.NotEqual("文章を修正", Run("inspect")["steps"][1]["headline"].GetValue<string>());
            Run("delete-step", new JsonObject { ["seq"] = 4 });
            Assert.Equal(3, ((JsonArray)Run("inspect")["steps"]).Count);
            var exported = Run("export", new JsonObject { ["embed"] = true, ["marks"] = "none" });
            var files = ((JsonArray)exported["files"]).Select(node => node.GetValue<string>()).ToArray();
            Assert.Equal(2, files.Length); Assert.All(files, file => Assert.True(File.Exists(file)));
            Assert.Contains("変更した記録", File.ReadAllText(files.Single(file => file.EndsWith(".md"))));
        }

        [Fact]
        public void テンプレートの追加読込保存削除検査とプレビューを隔離する()
        {
            using var scope = new AutomationScope(); var session = scope.Session();
            JsonObject Run(string action, JsonObject data = null)
            {
                data ??= new JsonObject(); data["schemaVersion"] = 1; data["action"] = action;
                if (action == "read" || action == "create" || action == "save" || action == "delete") data["name"] = "独自様式";
                return BasicAutomation.Execute("template", data, scope.Workspace);
            }
            Run("create", new JsonObject { ["text"] = "# {{caption}}\n{{#each steps}}{{headline}}\n{{#endeach}}" });
            Assert.Single((JsonArray)Run("list")["names"]);
            Assert.StartsWith("#", Run("read")["text"].GetValue<string>());
            Run("save", new JsonObject { ["text"] = "## {{caption}}" });
            var saved = Run("read")["text"].GetValue<string>();
            Assert.Throws<InvalidOperationException>(() => Run("save", new JsonObject { ["text"] = "{{#if broken}}" }));
            Assert.Equal(saved, Run("read")["text"].GetValue<string>());
            var preview = Run("preview", new JsonObject { ["session"] = session, ["text"] = "# {{caption}}" });
            Assert.Contains("受注登録", preview["markdown"].GetValue<string>());
            Assert.NotNull(Run("check", new JsonObject { ["text"] = "{{#if invalid}}" })["error"]);
            Assert.True(Run("delete")["deleted"].GetValue<bool>());
        }

        [Fact]
        public void 設定を専用領域へ保存復元し通常の静的設定を戻す()
        {
            using var scope = new AutomationScope();
            var before = AppSettings.SettingsDirectoryOverride; var passive = AppSettings.Passive;
            var response = BasicAutomation.Execute("settings", new JsonObject
            {
                ["schemaVersion"] = 1, ["action"] = "save", ["settings"] = new JsonObject { ["Mode"] = "Active", ["Caption"] = "CLIの設定", ["BeforeMs"] = 75, ["OutDir"] = Path.GetTempPath() },
            }, scope.Workspace);
            Assert.Equal(before, AppSettings.SettingsDirectoryOverride); Assert.Equal(passive, AppSettings.Passive);
            var loaded = BasicAutomation.Execute("settings", new JsonObject { ["schemaVersion"] = 1, ["action"] = "load" }, scope.Workspace)["settings"];
            Assert.Equal("Active", loaded["Mode"].GetValue<string>()); Assert.Equal("CLIの設定", loaded["Caption"].GetValue<string>());
            Assert.StartsWith(scope.Root, loaded["OutDir"].GetValue<string>());
            Assert.True(File.Exists(scope.Workspace.Resolve("settings/settings.json")));
        }

        [Theory]
        [InlineData("../outside.png")]
        [InlineData("C:/outside.png")]
        [InlineData("img/file.png:alternate")]
        public void 不正な画像参照では記録編集を始めない(string path)
        {
            using var scope = new AutomationScope(); var session = scope.Session();
            var events = File.ReadAllLines(Path.Combine(session, "events.jsonl"));
            var first = JsonNode.Parse(events[0]); first["before"]["file"] = path; events[0] = first.ToJsonString();
            File.WriteAllLines(Path.Combine(session, "events.jsonl"), events);
            var before = File.ReadAllBytes(Path.Combine(session, "session.json"));
            Assert.Throws<ArgumentException>(() => BasicAutomation.Execute("session", new JsonObject { ["schemaVersion"] = 1, ["action"] = "metadata", ["session"] = session, ["caption"] = "破壊しない" }, scope.Workspace));
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(session, "session.json")));
        }

        static string[] WorkspaceState(string root) => Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path) + "/")
            .Concat(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(root, path) + ":" +
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))))
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();

        [Theory, Trait("CaseId", "BASIC-VALIDATE-001")]
        [InlineData("session", "{\"action\":\"metadata\",\"caption\":\"変更しない\",\"captoin\":\"不明\"}")]
        [InlineData("session", "{\"action\":\"metadata\"}")]
        [InlineData("session", "{\"action\":\"metadata\",\"caption\":null}")]
        [InlineData("session", "{\"action\":\"edit\",\"seq\":2,\"hedline\":\"誤字\"}")]
        [InlineData("session", "{\"action\":\"edit\",\"seq\":2,\"headline\":\"見出し\"}")]
        [InlineData("session", "{\"action\":\"edit\",\"seq\":2,\"body\":\"本文\"}")]
        [InlineData("session", "{\"action\":\"edit\",\"seq\":2,\"headline\":null,\"body\":\"本文\"}")]
        [InlineData("session", "{\"action\":\"edit\",\"seq\":2,\"headline\":\"見出し\",\"body\":12}")]
        [InlineData("session", "{\"action\":\"reset\",\"seq\":0}")]
        [InlineData("session", "{\"action\":\"delete-step\",\"seq\":\"2\"}")]
        [InlineData("session", "{\"action\":\"inspect\",\"session\":null}")]
        [InlineData("session", "{\"action\":\"inspect\",\"session\":123}")]
        [InlineData("session", "{\"action\":\"export\",\"templates\":\"user\"}")]
        [InlineData("session", "{\"action\":\"export\",\"templates\":[]}")]
        [InlineData("session", "{\"action\":\"export\",\"templates\":[\"../private\"]}")]
        [InlineData("session", "{\"action\":\"export\",\"md\":false,\"html\":false}")]
        [InlineData("session", "{\"action\":\"export\",\"embed\":null}")]
        [InlineData("session", "{\"action\":\"export\",\"radius\":null}")]
        [InlineData("session", "{\"action\":\"export\",\"marks\":\"typo\"}")]
        [InlineData("template", "{\"action\":\"create\",\"name\":\"新規\",\"txet\":\"誤字\"}")]
        [InlineData("template", "{\"action\":\"create\",\"name\":\" leading\"}")]
        [InlineData("template", "{\"action\":\"read\"}")]
        [InlineData("template", "{\"action\":\"save\",\"name\":\"既存\",\"text\":null}")]
        [InlineData("template", "{\"action\":\"check\",\"text\":123}")]
        [InlineData("template", "{\"action\":\"preview\",\"text\":\"# 説明\"}")]
        [InlineData("settings", "{\"action\":\"save\"}")]
        [InlineData("settings", "{\"action\":\"load\",\"setings\":{}}")]
        [InlineData("session", "{\"action\":\"inspect\",\"schemaVersion\":2}")]
        [InlineData("session", "{\"action\":\"inspect\",\"schemaVersion\":null}")]
        [InlineData("session", "{\"action\":123}")]
        [InlineData("session", "{}")]
        public void 不正な操作入力は事前検証で拒否し保存済み編集と作業領域を変えない(string area, string json)
        {
            using var scope = new AutomationScope(); string session = scope.Session();
            OpRec.ReportGenerator.SaveStepEdit(session, 2, "残す見出し", "残す説明");
            var request = JsonNode.Parse(json).AsObject();
            if (!request.ContainsKey("schemaVersion")) request["schemaVersion"] = 1;
            if (area == "session" && !request.ContainsKey("session")) request["session"] = session;
            var previous = WorkspaceState(scope.Root);
            Assert.Throws<ArgumentException>(() => BasicAutomation.Execute(area, request, scope.Workspace));
            Assert.Equal(previous, WorkspaceState(scope.Root));
        }

        [Fact, Trait("CaseId", "BASIC-VALIDATE-002")]
        public void 記録名と解説はGUIと同様に外側だけを空白除去し省略した項目を保つ()
        {
            using var scope = new AutomationScope(); string session = scope.Session();
            var result = BasicAutomation.Execute("session", new JsonObject
            {
                ["schemaVersion"] = 1, ["action"] = "metadata", ["session"] = session, ["caption"] = " \r\n変更した名前\t ",
            }, scope.Workspace);
            Assert.Equal("変更した名前", (string)result["caption"]); Assert.Equal("テスト用の記録です。", (string)result["description"]);
            result = BasicAutomation.Execute("session", new JsonObject
            {
                ["schemaVersion"] = 1, ["action"] = "metadata", ["session"] = session, ["description"] = " \r\n説明1\r\n説明2\t ",
            }, scope.Workspace);
            Assert.Equal("変更した名前", (string)result["caption"]); Assert.Equal("説明1\r\n説明2", (string)result["description"]);
        }

        [Theory, Trait("CaseId", "BASIC-VALIDATE-003")]
        [InlineData("null")]
        [InlineData("[]")]
        [InlineData("{\"Captoin\":\"誤字\"}")]
        [InlineData("{\"Caption\":123}")]
        [InlineData("{\"BeforeMs\":\"75\"}")]
        [InlineData("{\"OutputHtml\":null}")]
        [InlineData("{\"Rules\":{\"click\":{\"Recrod\":true}}}")]
        public void 設定保存の項目誤りや型崩れは既存設定を既定値へ戻さず拒否する(string json)
        {
            using var scope = new AutomationScope();
            BasicAutomation.Execute("settings", new JsonObject
            {
                ["schemaVersion"] = 1, ["action"] = "save", ["settings"] = new JsonObject { ["Caption"] = "維持する設定", ["Mode"] = "Window" },
            }, scope.Workspace);
            var previous = WorkspaceState(scope.Root); var settingsDirectory = AppSettings.SettingsDirectoryOverride; bool passive = AppSettings.Passive;
            Assert.Throws<ArgumentException>(() => BasicAutomation.Execute("settings", new JsonObject
            {
                ["schemaVersion"] = 1, ["action"] = "save", ["settings"] = JsonNode.Parse(json),
            }, scope.Workspace));
            Assert.Equal(previous, WorkspaceState(scope.Root)); Assert.Equal(settingsDirectory, AppSettings.SettingsDirectoryOverride); Assert.Equal(passive, AppSettings.Passive);
        }

        [Fact, Trait("CaseId", "BASIC-VALIDATE-004")]
        public void 旧設定の移行読込みと設定結果の再保存は維持する()
        {
            using var scope = new AutomationScope();
            scope.Workspace.WriteJson("settings/settings.json", new JsonObject { ["Caption"] = "旧設定", ["FutureSetting"] = true });
            var loaded = BasicAutomation.Execute("settings", new JsonObject { ["schemaVersion"] = 1, ["action"] = "load" }, scope.Workspace);
            Assert.Equal("旧設定", (string)loaded["settings"]["Caption"]);
            var saved = BasicAutomation.Execute("settings", new JsonObject
            {
                ["schemaVersion"] = 1, ["action"] = "save", ["settings"] = loaded["settings"].DeepClone(),
            }, scope.Workspace);
            Assert.Equal("旧設定", (string)saved["settings"]["Caption"]);
            Assert.True(JsonNode.Parse(File.ReadAllText(scope.Workspace.Resolve("settings/settings.json")))["OutputHtml"].GetValue<bool>());
        }

        [Fact, Trait("CaseId", "BASIC-VALIDATE-005")]
        public void 直接APIでもschemaVersionを省略して既存編集を変更できない()
        {
            using var scope = new AutomationScope(); string session = scope.Session(); var previous = WorkspaceState(scope.Root);
            Assert.Throws<ArgumentException>(() => BasicAutomation.Execute("session", new JsonObject
            {
                ["action"] = "metadata", ["session"] = session, ["caption"] = "実行しない",
            }, scope.Workspace));
            Assert.Equal(previous, WorkspaceState(scope.Root));
        }

        [Fact, Trait("CaseId", "BASIC-VALIDATE-006")]
        public void 不明な項目を含む実EXE要求は失敗JSONと終了コードで保存済み文章を保つ()
        {
            using var scope = new AutomationScope(); string session = scope.Session();
            OpRec.ReportGenerator.SaveStepEdit(session, 2, "維持する見出し", "維持する本文");
            var before = File.ReadAllBytes(Path.Combine(session, "edits.json"));
            var result = scope.Run("session", new JsonObject { ["action"] = "edit", ["session"] = session, ["seq"] = 2, ["hedline"] = "誤字" });
            Assert.Equal(2, result.Code); Assert.False(result.Result["success"].GetValue<bool>());
            Assert.Equal("ArgumentException", (string)result.Result["error"]["type"]); Assert.Equal(before, File.ReadAllBytes(Path.Combine(session, "edits.json")));
        }

        [Fact, Trait("CaseId", "BASIC-VALIDATE-007")]
        public async Task 出力する独自テンプレート自体の再解析ポイントを読み込む前に拒否する()
        {
            using var scope = new AutomationScope(); string session = scope.Session();
            string templates = scope.Workspace.EnsureDirectory("templates"), target = scope.Workspace.EnsureDirectory("link-target");
            string original = Path.Combine(target, "keep.txt"); File.WriteAllText(original, "リンク先を変更しない");
            string link = Path.Combine(templates, "linked.md");
            string Quote(string value) => "'" + value.Replace("'", "''") + "'";
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "New-Item -ItemType Junction -Path " + Quote(link) + " -Target " + Quote(target) + " -ErrorAction Stop | Out-Null" }) start.ArgumentList.Add(arg);
            using (var process = Process.Start(start))
            {
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException("試験用リンクを作成できませんでした。"); }
                await output; Assert.True(process.ExitCode == 0, await error);
            }
            try
            {
                Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
                var before = File.ReadAllBytes(Path.Combine(session, "session.json"));
                Assert.Throws<ArgumentException>(() => BasicAutomation.Execute("session", new JsonObject
                {
                    ["schemaVersion"] = 1, ["action"] = "export", ["session"] = session,
                    ["templates"] = new JsonArray("linked"), ["templateDirectory"] = templates,
                }, scope.Workspace));
                Assert.False(Directory.Exists(Path.Combine(scope.Root, "exports")));
                Assert.Equal(before, File.ReadAllBytes(Path.Combine(session, "session.json"))); Assert.Equal("リンク先を変更しない", File.ReadAllText(original));
            }
            finally { if (Directory.Exists(link)) Directory.Delete(link); }
        }

        [Fact, Trait("CaseId", "BASIC-VALIDATE-008")]
        public void 壊れた編集JSONを含む記録の削除はイベントを書き換える前に止める()
        {
            using var scope = new AutomationScope(); string session = scope.Session();
            File.WriteAllText(Path.Combine(session, "edits.json"), "{invalid"); var previous = WorkspaceState(scope.Root);
            Assert.ThrowsAny<System.Text.Json.JsonException>(() => BasicAutomation.Execute("session", new JsonObject
            {
                ["schemaVersion"] = 1, ["action"] = "delete-step", ["session"] = session, ["seq"] = 2,
            }, scope.Workspace));
            Assert.Equal(previous, WorkspaceState(scope.Root));
        }

        [Fact, Trait("CaseId", "BASIC-VALIDATE-009")]
        public void 独自テンプレートの出力と旧様式名からの互換切替を保つ()
        {
            using var scope = new AutomationScope(); string session = scope.Session();
            string templates = scope.Workspace.EnsureDirectory("templates"); File.WriteAllText(Path.Combine(templates, "user.md"), "# {{caption}}\nCLIの独自様式");
            var result = BasicAutomation.Execute("session", new JsonObject
            {
                ["schemaVersion"] = 1, ["action"] = "export", ["session"] = session,
                ["templates"] = new JsonArray("user-table"), ["templateDirectory"] = templates, ["html"] = false,
            }, scope.Workspace);
            var file = Assert.Single(result["files"].AsArray()).GetValue<string>();
            Assert.Contains("受注登録", File.ReadAllText(file)); Assert.Contains("CLIの独自様式", File.ReadAllText(file));
        }
    }

    public class AutomationCommandTests
    {
        [Fact]
        public void 実EXEの自動操作はGUI起動なしに結果JSONと終了コードを返す()
        {
            using var scope = new AutomationScope(); var session = scope.Session();
            var result = scope.Run("session", new JsonObject { ["action"] = "metadata", ["session"] = session, ["caption"] = "日本語の記録" });
            Assert.Equal(0, result.Code); Assert.True(result.Result["success"].GetValue<bool>());
            Assert.Equal("日本語の記録", result.Result["result"]["caption"].GetValue<string>());
        }

        [Theory]
        [InlineData("session", "missing")]
        [InlineData("template", "missing")]
        [InlineData("settings", "missing")]
        public void 不明な操作はGUIダイアログなしで引数エラーを返す(string area, string action)
        {
            using var scope = new AutomationScope(); var session = scope.Session();
            var result = scope.Run(area, new JsonObject { ["action"] = action, ["session"] = session });
            Assert.Equal(2, result.Code); Assert.False(result.Result["success"].GetValue<bool>());
        }

        [Fact]
        public void ファイル不足は処理失敗として結果と非ゼロを返す()
        {
            using var scope = new AutomationScope();
            var result = scope.Run("session", new JsonObject { ["action"] = "inspect", ["session"] = "missing" });
            Assert.Equal(1, result.Code); Assert.Equal("FileNotFoundException", result.Result["error"]["type"].GetValue<string>());
        }

        [Fact]
        public void 不正な引数とJSONは通常起動を呼ばない()
        {
            using var scope = new AutomationScope();
            Assert.Equal(0, scope.Process("--automation", "help").Code);
            Assert.Equal(2, scope.Process("--automation", "unknown").Code);
            Assert.Equal(2, scope.Process("--automation", "session", "--input").Code);
            var input = scope.Workspace.Resolve("bad.json"); File.WriteAllText(input, "{invalid");
            var failed = scope.Process("--automation", "session", "--input", input, "--workspace", scope.Root);
            Assert.Equal(2, failed.Code); Assert.False(JsonNode.Parse(failed.Output)["success"].GetValue<bool>());
        }

        [Fact]
        public void 入力と結果の同一指定を処理前に拒否する()
        {
            using var scope = new AutomationScope(); var session = scope.Session();
            var input = scope.Workspace.Resolve("request.json");
            File.WriteAllText(input, new JsonObject { ["schemaVersion"] = 1, ["action"] = "metadata", ["session"] = session, ["caption"] = "実行しない" }.ToJsonString());
            var before = File.ReadAllBytes(Path.Combine(session, "session.json"));
            var result = scope.Process("--automation", "session", "--input", input, "--workspace", scope.Root, "--result", input);
            Assert.Equal(2, result.Code); Assert.Equal(before, File.ReadAllBytes(Path.Combine(session, "session.json")));
        }

        [Fact]
        public void 試験領域は通常フォルダーや外部への書込みを拒否する()
        {
            using var scope = new AutomationScope();
            Assert.Throws<ArgumentException>(() => scope.Workspace.Resolve("../escape.json"));
            Assert.Throws<ArgumentException>(() => scope.Workspace.Resolve("image.png:alternate"));
            Assert.Throws<ArgumentException>(() => scope.Workspace.Resolve(AutomationWorkspace.MarkerName));
            var nonempty = scope.Workspace.EnsureDirectory("nonempty"); var keep = Path.Combine(nonempty, "keep.txt"); File.WriteAllText(keep, "既存ファイル");
            Assert.Throws<ArgumentException>(() => new AutomationWorkspace(nonempty)); Assert.Equal("既存ファイル", File.ReadAllText(keep));
            Assert.Throws<ArgumentException>(() => new AutomationWorkspace(Path.GetPathRoot(scope.Root)));
        }

        [Fact]
        public void 結果の保存先に記録や既存入力を指定しても処理前に拒否する()
        {
            using var scope = new AutomationScope(); var session = scope.Session();
            var input = scope.Workspace.Resolve("request.json");
            File.WriteAllText(input, new JsonObject { ["schemaVersion"] = 1, ["action"] = "metadata", ["session"] = session, ["caption"] = "実行しない" }.ToJsonString());
            var before = File.ReadAllBytes(Path.Combine(session, "session.json"));
            Assert.Equal(2, scope.Process("--automation", "session", "--input", input, "--workspace", scope.Root, "--result", Path.Combine(session, "session.json")).Code);
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(session, "session.json")));
            scope.Workspace.WriteJson("results/existing.json", new JsonObject { ["original"] = true });
            Assert.Equal(2, scope.Process("--automation", "session", "--input", input, "--workspace", scope.Root, "--result", "results/existing.json").Code);
            Assert.True(JsonNode.Parse(File.ReadAllText(scope.Workspace.Resolve("results/existing.json")))["original"].GetValue<bool>());
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(session, "session.json")));
        }
    }
}
