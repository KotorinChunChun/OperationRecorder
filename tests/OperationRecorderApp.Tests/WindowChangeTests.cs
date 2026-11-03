using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using OpRec;
using OperationRecorderApp.Replay;
using Xunit;

namespace OperationRecorderApp.Tests
{
    public class WindowChangeTests
    {
        [Theory]
        [InlineData("editor.editor", "editor.editor", true)]
        [InlineData("editor.editor.exe", "EDITOR.EDITOR", true)]
        [InlineData("editor.exe", "other", false)]
        [InlineData(null, "editor", false)]
        public void アプリのプロセス名のドットを拡張子と誤認しない(string configured, string actual, bool expected)
            => Assert.Equal(expected, WindowChangeTracker.ProcessMatches(configured, actual));

        [Theory]
        [InlineData("Alt+Tab", true)]
        [InlineData("Alt+Shift+Tab", true)]
        [InlineData("Win+Tab", true)]
        [InlineData("Win+1", true)]
        [InlineData("Ctrl+N", false)]
        public void 切り替えキーの修飾キーと方向を判定する(string key, bool expected)
            => Assert.Equal(expected, WindowChangeTracker.IsNavigationKey(key));

        [Fact]
        public void 開始時の画面と同じ画面の通知を手順にしない()
        {
            var tracker = new WindowChangeTracker(); tracker.Prime(new long[] { 1, 2 }, 1);
            Assert.Null(tracker.Observe(1, true, 10, true));
            Assert.Null(tracker.Observe(1, false, 20, true));
            Assert.Equal("switch", tracker.Observe(2, false, 30, true));
        }

        [Fact]
        public void 表示と前面の通知をまとめて再切り替えを記録する()
        {
            var tracker = new WindowChangeTracker(); tracker.Prime(new long[] { 1 }, 1);
            Assert.Equal("open", tracker.Observe(2, true, 100, true));
            Assert.Null(tracker.Observe(2, false, 110, true));
            Assert.Null(tracker.Observe(2, true, 120, true));
            Assert.Equal("switch", tracker.Observe(1, false, 200, true));
            Assert.Equal("switch", tracker.Observe(2, false, 300, true));
        }

        [Fact]
        public void 対象外を記録せず破棄後のハンドル再利用を新規として扱う()
        {
            var tracker = new WindowChangeTracker(); tracker.Prime(new long[] { 1 }, 1);
            Assert.Null(tracker.Observe(2, true, 100, false));
            Assert.Null(tracker.Observe(2, false, 110, false));
            Assert.Equal("switch", tracker.Observe(1, false, 200, true));
            tracker.Forget(2);
            Assert.Equal("open", tracker.Observe(2, true, 300, true));
        }

        [Theory]
        [InlineData("click", 0, 1000, true, false, true)]
        [InlineData("middleclick", 0, 500, true, false, true)]
        [InlineData("click", 0, 1001, true, false, false)]
        [InlineData("click", 10, 0, true, false, false)]
        [InlineData("click", 0, 500, false, false, false)]
        [InlineData("key", 0, 500, false, true, true)]
        [InlineData("wheel", 0, 100, true, false, false)]
        [InlineData("drag", 0, 100, true, false, false)]
        public void 関連付けは時間と操作と対象関係を検査する(string kind, long action, long observed, bool sameProcess, bool navigation, bool expected)
            => Assert.Equal(expected, WindowChangeTracker.CanAssociate(kind, action, observed, sameProcess, navigation));

        [Theory]
        [InlineData("window-open", "window", "「設定」画面が表示されます")]
        [InlineData("window-switch", "window", "「設定」画面に切り替えます")]
        [InlineData("window-open", "taskbar", "タスクバーから「販売管理」を「クリック」します。「設定」画面が表示されます")]
        [InlineData("window-switch", "taskbar", "タスクバーから「販売管理」を「クリック」します。「設定」画面が表示されます")]
        public void 観測手順を文にしてリプレイでは画面待機に変換する(string kind, string origin, string expected)
        {
            using var session = new TestSession();
            string path = Path.Combine(session.Dir, "events.jsonl"); var lines = File.ReadAllLines(path);
            var first = JsonNode.Parse(lines[0]); first["kind"] = kind; first["window"]["title"] = "設定";
            first["windowEvent"] = new JsonObject { ["type"] = kind == "window-open" ? "open" : "switch",
                ["origin"] = origin, ["window"] = first["window"].DeepClone(), ["label"] = "販売管理" };
            lines[0] = first.ToJsonString(); File.WriteAllLines(path, lines);
            string before = File.ReadAllText(path);
            var text = ReportGenerator.Render(new ReportGenerator.Options { SessionDir = session.Dir,
                TemplateDir = Path.Combine(AppContext.BaseDirectory, "templates") }).Single().Value;
            Assert.Contains("## 手順 1. " + expected, text);
            var psr = ReportGenerator.Render(new ReportGenerator.Options { SessionDir = session.Dir, Templates = new[] { "psr" },
                TemplateDir = Path.Combine(AppContext.BaseDirectory, "templates") }).Single().Value;
            Assert.DoesNotContain("window-open", psr); Assert.DoesNotContain("window-switch", psr);
            Assert.Contains("「設定」画面", psr);
            var step = ReplayImporter.Import(session.Dir).Steps[0];
            Assert.Equal("wait", step.Kind); Assert.Null(step.BeforeImage);
            Assert.Equal(before, File.ReadAllText(path));
        }

        [Theory]
        [InlineData("action", "「販売管理」画面の「登録」ボタンを「クリック」します。「設定」画面が表示されます")]
        [InlineData("switch", "「設定」画面に切り替えます")]
        public void 操作後の観測を同じ手順へ出力して別画面の画像を元画面で照合しない(string origin, string expected)
        {
            using var session = new TestSession();
            string path = Path.Combine(session.Dir, "events.jsonl"); var lines = File.ReadAllLines(path);
            var first = JsonNode.Parse(lines[0]); var destination = first["window"].DeepClone();
            destination["title"] = "設定"; destination["hwnd"] = "0x9876";
            first["windowEvent"] = new JsonObject { ["type"] = origin == "switch" ? "switch" : "open", ["origin"] = origin,
                ["relation"] = "observedAfterInput", ["window"] = destination };
            lines[0] = first.ToJsonString(); File.WriteAllLines(path, lines);
            var text = ReportGenerator.Render(new ReportGenerator.Options { SessionDir = session.Dir,
                TemplateDir = Path.Combine(AppContext.BaseDirectory, "templates") }).Single().Value;
            Assert.Contains("## 手順 1. " + expected, text);
            Assert.Equal(4, ReportGenerator.ListSteps(session.Dir).Count);
            Assert.Equal("click", ReplayImporter.Import(session.Dir).Steps[0].Kind);
            Assert.Null(ReplayImporter.Import(session.Dir).Steps[0].AfterImage);
        }
    }
}
