using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using OpRec;
using Xunit;

namespace OperationRecorderApp.Tests
{
    // 記録一覧の「この手順を削除」
    public class SessionEditorTests : IDisposable
    {
        readonly TestSession s = new TestSession();
        public void Dispose() => s.Dispose();

        [Fact]
        public void 手順を消すと後ろの番号が繰り上がる()
        {
            var before = ReportGenerator.ListSteps(s.Dir);
            Assert.Equal(4, before.Count);
            string third = before[2].Headline;

            Assert.True(SessionEditor.DeleteStep(s.Dir, 2));

            var after = ReportGenerator.ListSteps(s.Dir);
            Assert.Equal(new[] { 1, 2, 3 }, after.Select(x => x.Number));
            Assert.Equal(third, after[1].Headline);                    // 前の手順 3 が手順 2 になる
            Assert.DoesNotContain(after, x => x.Seq == 2);
        }

        [Fact]
        public void 消した手順の画像と手直しを消し手順数を直す()
        {
            ReportGenerator.SaveStepEdit(s.Dir, 2, "直した見出し", null);
            ReportGenerator.SaveStepEdit(s.Dir, 3, "残す見出し", null);
            SessionEditor.DeleteStep(s.Dir, 2);

            Assert.False(File.Exists(s.ImagePath(2, "before")));
            Assert.False(File.Exists(s.ImagePath(2, "after")));
            Assert.True(File.Exists(s.ImagePath(3, "before")));
            var edits = JsonNode.Parse(File.ReadAllText(Path.Combine(s.Dir, "edits.json")))["steps"].AsObject();
            Assert.False(edits.ContainsKey("2"));
            Assert.True(edits.ContainsKey("3"));
            var session = JsonNode.Parse(File.ReadAllText(Path.Combine(s.Dir, "session.json")));
            Assert.Equal(3, (int)session["eventCount"]);
            Assert.Equal(3, File.ReadAllLines(Path.Combine(s.Dir, "events.jsonl")).Count(l => l.Trim().Length > 0));
        }

        [Fact]
        public void 画像の消し方を渡せる()
        {
            var deleted = new List<string>();
            SessionEditor.DeleteStep(s.Dir, 1, p => deleted.Add(Path.GetFileName(p)));
            Assert.Equal(new[] { "0001_before.png", "0001_after.png" }, deleted);
            Assert.True(File.Exists(s.ImagePath(1, "before")));   // 渡した処理だけが呼ばれる(ここでは消していない)
        }

        [Fact]
        public void 無い手順は消さない()
        {
            Assert.False(SessionEditor.DeleteStep(s.Dir, 99));
            Assert.Equal(4, ReportGenerator.ListSteps(s.Dir).Count);
        }

        [Fact]
        public void 消したあとの出力にも反映される()
        {
            SessionEditor.DeleteStep(s.Dir, 1);
            var mds = new List<string>();
            RecordingController.Export(Path.Combine(AppContext.BaseDirectory, "templates"), s.Dir, new[] { "user" }, null, false, true, false, null, null, mds, new List<string>());
            string md = File.ReadAllText(mds[0]);
            Assert.Equal(3, TestSession.Count(md, "## 手順 "));
            Assert.DoesNotContain("0001_before.png", md);
            Assert.Contains("## 手順 1.", md);
        }
    }

    // ドラッグの矢印
    public class DragArrowTests
    {
        [Fact]
        public void 丸の縁から離した位置へ()
        {
            Assert.True(DragArrow.TryGet(new Point(100, 100), new Point(100, 300), 14, out var from, out var to, out var head));
            Assert.Equal(new PointF(100, 114), from);
            Assert.Equal(new PointF(100, 300), to);
            Assert.InRange(head, 12f, 26f);
        }

        [Fact]
        public void ほとんど動いていなければ描かない()
        {
            Assert.False(DragArrow.TryGet(new Point(100, 100), new Point(105, 108), 14, out _, out _, out _));
        }

        [Fact]
        public void 矢印の先は離した位置で底辺は線に直角()
        {
            var tri = DragArrow.Head(new PointF(0, 0), new PointF(100, 0), 20);
            Assert.Equal(new PointF(100, 0), tri[0]);
            Assert.Equal(80f, tri[1].X, 3);
            Assert.Equal(80f, tri[2].X, 3);
            Assert.Equal(-tri[1].Y, tri[2].Y, 3);   // 線をはさんで対称
        }
    }

    // テンプレートのエディタ(追加・削除・保存・誤りの確認・プレビュー)
    public class TemplateStoreTests : IDisposable
    {
        readonly TestSession s = new TestSession();
        readonly string dir;

        public TemplateStoreTests()
        {
            dir = Path.Combine(s.Root, "templates");
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "templates"), "*.md")) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        }

        public void Dispose() => s.Dispose();

        [Theory]
        [InlineData("", "名前を入れて")]
        [InlineData("a/b", "使えない文字")]
        [InlineData("a,b", "使えない文字")]
        [InlineData("user", "もうあります")]
        public void 使えない名前(string name, string expected) => Assert.Contains(expected, TemplateStore.CheckNewName(dir, name));

        [Fact]
        public void 使える名前() => Assert.Null(TemplateStore.CheckNewName(dir, "社内様式"));

        [Fact]
        public void もとのテンプレートをもとに追加して一覧に出る()
        {
            TemplateStore.Create(dir, "社内様式", TemplateStore.Read(dir, "user"));
            Assert.Contains("社内様式", TemplateStore.Names(dir));
            Assert.Equal(TemplateStore.Read(dir, "user"), TemplateStore.Read(dir, "社内様式"));
        }

        [Fact]
        public void 空から追加すると骨組みが入り説明も出る()
        {
            TemplateStore.Create(dir, "簡易", null);
            Assert.Null(TemplateStore.Check(TemplateStore.Read(dir, "簡易")));
            var item = OutputChoices.LoadTemplates(dir).Single(t => t.Name == "簡易");
            Assert.Contains("簡易 の説明", item.Desc);
        }

        [Fact]
        public void 書き方の誤りは保存しない()
        {
            Assert.NotNull(TemplateStore.Check("{{#if caption}}閉じていない"));
            Assert.Null(TemplateStore.Check("{{#if caption}}あり{{#endif}}"));
            string original = TemplateStore.Read(dir, "user");
            Assert.Throws<InvalidOperationException>(() => TemplateStore.Save(dir, "user", "{{#each steps}}"));
            Assert.Equal(original, TemplateStore.Read(dir, "user"));
        }

        [Fact]
        public void 保存できる()
        {
            TemplateStore.Save(dir, "user", "# {{caption}}\n");
            Assert.Equal("# {{caption}}\n", TemplateStore.Read(dir, "user"));
        }

        [Fact]
        public void 削除すると一覧から消える()
        {
            TemplateStore.Delete(dir, "psr", false);
            Assert.DoesNotContain("psr", TemplateStore.Names(dir));
            TemplateStore.Delete(dir, "psr", false);   // 無くても失敗しない
        }

        [Fact]
        public void 保存していない中身でプレビューできる()
        {
            string md = TemplateStore.Preview(s.Dir, "# {{caption}}の記録\n{{#each steps}}\n## {{number}}. {{headline}}\n{{images.verticalMerged}}\n{{#endeach}}\n");
            Assert.StartsWith("# 受注登録の記録", md);
            Assert.Equal(4, TestSession.Count(md, "\n## "));
            Assert.Contains("data:image/png;base64,", md);     // 画像は埋め込む(どこに置いても表示できる)
            Assert.Contains("画面の変化なし", md);              // テンプレートで選んだ並べ方が効く
            Assert.False(File.Exists(Path.Combine(dir, "preview.md")));   // テンプレートのフォルダには何も書かない
        }

        [Fact]
        public void 誤りのある中身はプレビューしない()
        {
            Assert.Throws<InvalidOperationException>(() => TemplateStore.Preview(s.Dir, "{{#if x}}"));
        }
    }
}
