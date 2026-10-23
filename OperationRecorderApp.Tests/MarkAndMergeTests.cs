using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using OpRec;
using Xunit;

namespace OperationRecorderApp.Tests
{
    // 出力するときに、操作前の画像へ印(ポインタ・丸・矢印・枠)を合成する
    public class MarkTests : IDisposable
    {
        readonly TestSession s = new TestSession(marksOutput: true);
        public void Dispose() => s.Dispose();

        static string TemplateDir => Path.Combine(AppContext.BaseDirectory, "templates");

        // クリックは画面 (150,120)、画像の範囲は (100,100) から → 画像の中では (50,20)。丸の半径 14 なら (64,20) が丸の線の上
        static readonly Point RingPoint = new Point(64, 20);

        string ExportMd(MarkOptions marks, bool embed = false, TestSession session = null)
        {
            var mds = new List<string>();
            RecordingController.Export(TemplateDir, (session ?? s).Dir, new[] { "user" }, null, embed, true, false, null, null, mds, new List<string>(), marks);
            return File.ReadAllText(mds[0]);
        }

        static Color Pixel(string path, Point p)
        {
            using (var b = new Bitmap(new MemoryStream(File.ReadAllBytes(path)))) return b.GetPixel(p.X, p.Y);
        }

        static bool IsRed(Color c) => c.R > 200 && c.G < 90 && c.B < 90;

        [Fact]
        public void 印を合成した画像は別のフォルダに作り元の画像は変えない()
        {
            string md = ExportMd(MarkOptions.Default());
            string marked = Path.Combine(s.Dir, TestSession.Name + "_marked", "0001_before.png");
            Assert.Contains(TestSession.Name + "_marked/0001_before.png", md);
            Assert.True(IsRed(Pixel(marked, RingPoint)), "クリック位置の丸が描かれていない");
            Assert.False(IsRed(Pixel(s.ImagePath(1, "before"), RingPoint)), "元の画像に描いてしまった");
            Assert.Contains("img/0001_after.png", md);   // 操作後の画像には描かない
        }

        [Fact]
        public void 部品の枠を描く()
        {
            ExportMd(MarkOptions.Parse("frame"));
            // 部品は画面 (110,110,80,24) → 画像の中では (10,10,80,24)。枠の左上の辺
            var c = Pixel(Path.Combine(s.Dir, TestSession.Name + "_marked", "0001_before.png"), new Point(40, 10));
            Assert.True(c.R > 200 && c.G > 100 && c.G < 180 && c.B < 80, "橙の枠が無い: " + c);
        }

        [Fact]
        public void マウスポインタを描く()
        {
            ExportMd(MarkOptions.Parse("pointer"));
            string marked = Path.Combine(s.Dir, TestSession.Name + "_marked", "0001_before.png");
            var inside = Pixel(marked, new Point(51, 30));   // ポインタの中(白)
            Assert.True(inside.R > 230 && inside.G > 230 && inside.B > 230, "ポインタが無い: " + inside);
            Assert.False(IsRed(Pixel(marked, RingPoint)));    // 丸は選んでいない
        }

        [Fact]
        public void 印なしなら元の画像を参照する()
        {
            string md = ExportMd(MarkOptions.None());
            Assert.Contains("img/0001_before.png", md);
            Assert.False(Directory.Exists(Path.Combine(s.Dir, TestSession.Name + "_marked")));
        }

        [Fact]
        public void 印を描いて保存していた古い記録には重ねない()
        {
            using (var old = new TestSession())
            {
                string md = ExportMd(MarkOptions.Default(), session: old);
                Assert.Contains("img/0001_before.png", md);
                Assert.False(Directory.Exists(Path.Combine(old.Dir, TestSession.Name + "_marked")));
                Assert.Null(ReportGenerator.ListSteps(old.Dir)[0].BeforeMarks);
            }
        }

        [Fact]
        public void 埋め込むときは印を描いた画像を埋め込む()
        {
            string md = ExportMd(MarkOptions.Default(), embed: true);
            string raw = Convert.ToBase64String(File.ReadAllBytes(s.ImagePath(1, "before")));
            Assert.DoesNotContain(raw, md);                                                       // 手順 1 の操作前は描いた画像
            Assert.Contains(Convert.ToBase64String(File.ReadAllBytes(s.ImagePath(1, "after"))), md);   // 操作後はそのまま
        }

        [Fact]
        public void 記録一覧の表示用に印の位置を返す()
        {
            var st = ReportGenerator.ListSteps(s.Dir)[0];
            Assert.Equal(new Point(50, 20), st.BeforeMarks.Click);
            Assert.Equal(new Rectangle(10, 10, 80, 24), st.BeforeMarks.Element);
            Assert.False(Directory.Exists(Path.Combine(s.Dir, TestSession.Name + "_marked")));   // 一覧を開くだけではファイルを作らない
        }

        [Fact]
        public void 部品の位置が無い記録はウィンドウ内の位置から求める()
        {
            var e = JsonNode.Parse("{\"screen\":{\"x\":150,\"y\":120},\"window\":{\"frame\":{\"x\":100,\"y\":100}},\"uia\":{\"boundsInWindow\":{\"x\":10,\"y\":10,\"w\":80,\"h\":24}}}").AsObject();
            var img = JsonNode.Parse("{\"area\":{\"x\":90,\"y\":95,\"w\":400,\"h\":300}}").AsObject();
            var spec = MarkSpec.FromEvent(e, img);
            Assert.Equal(new Point(60, 25), spec.Click);
            Assert.Equal(new Rectangle(20, 15, 80, 24), spec.Element);
        }

        [Theory]
        [InlineData("circle,arrow,frame", "circle,arrow,frame")]
        [InlineData("pointer, circle", "pointer,circle")]
        [InlineData("none", "none")]
        [InlineData("", "none")]
        public void 印の指定を読む(string text, string expected) => Assert.Equal(expected, MarkOptions.Parse(text).ToString());

        [Fact]
        public void 知らない印は誤り() => Assert.Throws<ArgumentException>(() => MarkOptions.Parse("circle,star"));

        [Fact]
        public void 既定は丸と矢印と枠でポインタは描かない()
        {
            Assert.Equal("circle,arrow,frame", MarkOptions.Default().ToString());
            var o = AppSettings.FromJson(null).ToMarkOptions();
            Assert.Equal("circle,arrow,frame", o.ToString());
            Assert.Equal(14, o.Radius);
        }

        [Fact]
        public void exeの引数で印を選べる()
        {
            var o = ExportCommand.Parse(new[] { "--export", s.Dir, "--marks", "pointer,frame", "--radius", "20" }, out string error);
            Assert.Null(error);
            Assert.Equal("pointer,frame", o.Marks.ToString());
            Assert.Equal(20, o.Marks.Radius);
            Assert.Null(ExportCommand.Parse(new[] { "--export", s.Dir, "--radius", "999" }, out error));
            Assert.Contains("--radius", error);
        }
    }

    // 2 つの記録(前の記録 A・取り直した記録 B)の合成
    public class MergeTests
    {
        // 操作の鍵だけを持つ手順(画面のクラス名・部品の ID)。x / text は毎回違ってよい値
        static JsonObject Step(string id, int x = 100, string text = null, string kind = "click") =>
            JsonNode.Parse("{\"kind\":\"" + kind + "\",\"button\":\"left\",\"screen\":{\"x\":" + x + ",\"y\":50},\"window\":{\"className\":\"MainWnd\",\"title\":\"画面\"}," +
                "\"uia\":{\"controlType\":\"Button\",\"automationId\":\"" + id + "\",\"className\":\"Btn\",\"name\":\"" + id + "\"}" +
                (text != null ? ",\"input\":{\"value\":\"" + text + "\"}" : "") + "}").AsObject();

        static List<JsonObject> Steps(params string[] ids) => ids.Select(i => Step(i)).ToList();

        // 案を「A1 B2 AB3 …」の形にする(入れないものは小文字)
        static string Show(IEnumerable<SessionMerger.Row> rows) => string.Join(" ", rows.Select(r =>
        {
            string t = r.Kind == SessionMerger.RowKind.OnlyA ? "A" + (r.A + 1) : r.Kind == SessionMerger.RowKind.OnlyB ? "B" + (r.B + 1) : "AB" + (r.A + 1) + (r.B + 1);
            return r.Include ? t : t.ToLowerInvariant();
        }));

        [Fact]
        public void クリック位置や入力した文字が違っても同じ操作とみなす()
        {
            Assert.Equal(SessionMerger.Signature(Step("ok", 100, "山田")), SessionMerger.Signature(Step("ok", 300, "佐藤")));
            Assert.NotEqual(SessionMerger.Signature(Step("ok")), SessionMerger.Signature(Step("cancel")));
            Assert.NotEqual(SessionMerger.Signature(Step("ok")), SessionMerger.Signature(Step("ok", kind: "doubleclick")));
        }

        [Fact]
        public void 途中から取り直して続けたら_前の記録の残りは入れない()
        {
            // A: 1 2 3 で 4 5 を間違えた / B: 3 から取り直して 6 まで
            var plan = SessionMerger.Plan(Steps("s1", "s2", "s3", "x4", "x5"), Steps("s3", "s4", "s5", "s6"), false);
            Assert.Equal("A1 A2 AB31 a4 a5 B2 B3 B4", Show(plan));
        }

        [Fact]
        public void 途中だけ取り直したら_前の記録の続きを残す()
        {
            // A: 3 を間違えた / B: 2〜4 だけやり直した(4 で終わっている)
            var plan = SessionMerger.Plan(Steps("s1", "s2", "x3", "s4", "s5"), Steps("s2", "s3", "s4"), false);
            Assert.Equal("A1 AB21 a3 B2 AB43 A5", Show(plan));
        }

        [Fact]
        public void 重なる手順が無ければ両方を順につなぐ()
        {
            Assert.Equal("A1 A2 B1 B2", Show(SessionMerger.Plan(Steps("a", "b"), Steps("c", "d"), false)));
        }

        [Fact]
        public void 両方にある手順はどちらを採るか選べる()
        {
            Assert.All(SessionMerger.Plan(Steps("s1"), Steps("s1"), true), r => Assert.True(r.UseA));
            Assert.All(SessionMerger.Plan(Steps("s1"), Steps("s1"), false), r => Assert.False(r.UseA));
        }

        [Fact]
        public void 合成すると番号を振り直し画像と手直しを写す()
        {
            using (var a = new TestSession())
            using (var b = new TestSession(marksOutput: true))
            {
                ReportGenerator.SaveStepEdit(b.Dir, 2, "B で直した見出し", null);
                var plan = SessionMerger.Plan(SessionMerger.LoadEvents(a.Dir), SessionMerger.LoadEvents(b.Dir), false);
                Assert.All(plan, r => Assert.Equal(SessionMerger.RowKind.Both, r.Kind));
                plan[0].UseA = true;        // 手順 1 だけ A を採る
                plan[2].Include = false;    // 手順 3 は入れない
                string outRoot = Path.Combine(a.Root, "merged");
                Directory.CreateDirectory(outRoot);

                string dir = SessionMerger.Merge(a.Dir, b.Dir, plan, outRoot);

                var steps = ReportGenerator.ListSteps(dir);
                Assert.Equal(new[] { 1, 2, 3 }, steps.Select(x => x.Number));
                Assert.Equal(new long[] { 1, 2, 3 }, steps.Select(x => x.Seq));
                Assert.Equal("B で直した見出し", steps[1].Headline);                       // B の手直しが新しい番号に付く
                Assert.True(File.Exists(Path.Combine(dir, "img", "0003_before.png")));      // 元の手順 4 の画像が 0003 になる
                Assert.False(File.Exists(Path.Combine(dir, "img", "0004_before.png")));
                var events = SessionMerger.LoadEvents(dir);
                Assert.Equal("A", (string)events[0]["mergedFrom"]);
                Assert.Equal("B", (string)events[1]["mergedFrom"]);
                // A は印を描いて保存していた古い記録なので、A から来た手順には出力するときに印を重ねない
                Assert.True((bool)events[0]["marksBaked"]);
                Assert.Null(events[1]["marksBaked"]);
                var session = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "session.json")));
                Assert.Equal("output", (string)session["marks"]);
                Assert.Equal(3, (int)session["eventCount"]);
                Assert.Contains("(合成)", Path.GetFileName(dir));
                Assert.Null(steps[0].BeforeMarks);
                Assert.NotNull(steps[1].BeforeMarks);
            }
        }
    }
}
