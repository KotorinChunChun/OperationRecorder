using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpRec;
using Xunit;

namespace OperationRecorderApp.Tests
{
    // 記録フォルダから md / HTML を出力する(アプリの「HTML で出力」「Markdown で出力」・保存直後の出力と同じ処理)
    public class ExportTests : IDisposable
    {
        readonly TestSession s = new TestSession();
        public void Dispose() => s.Dispose();

        static string TemplateDir => Path.Combine(AppContext.BaseDirectory, "templates");

        sealed class Result { public string Md, Html; public List<string> MdFiles = new List<string>(), HtmlFiles = new List<string>(); }

        // template は名前(templates フォルダ)か md のパス
        Result Export(string template = "user", bool embed = false, bool md = true, bool html = true)
        {
            var r = new Result();
            RecordingController.Export(TemplateDir, s.Dir, new[] { template }, null, embed, md, html, null, null, r.MdFiles, r.HtmlFiles);
            if (r.MdFiles.Count > 0) r.Md = File.ReadAllText(r.MdFiles[0]);
            if (r.HtmlFiles.Count > 0) r.Html = File.ReadAllText(r.HtmlFiles[0]);
            return r;
        }

        // 画像の並べ方だけを変えたテンプレートを作る(手順ごとに {{images.<名前>}} を出す)
        string ImagesTemplate(string variable)
        {
            string dir = Path.Combine(s.Root, "tpl");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "t_" + variable + ".md");
            File.WriteAllText(path, "# 手順書\n\n{{#each steps}}\n## 手順 {{number}}. {{headline}}\n\n{{" + variable + "}}\n\n{{#endeach}}\n");
            return path;
        }

        [Fact]
        public void userは操作前と操作後を左右に並べる()
        {
            var r = Export();
            Assert.Equal(4, TestSession.Count(r.Md, "| 操作前 | 操作後 |"));
            var step1 = TestSession.Steps(r.Md)[1];
            Assert.Contains("img/0001_before.png", step1);
            Assert.Contains("img/0001_after.png", step1);
            // HTML では、画像の表を横幅いっぱいに広げる
            Assert.Equal(4, TestSession.Count(r.Html, "<table class=\"images\">"));
            Assert.Equal(8, TestSession.Count(r.Html, "<img "));
            Assert.DoesNotContain("画面の変化なし", r.Md);
        }

        [Fact]
        public void 左右に並べる() =>
            Assert.Equal(4, TestSession.Count(Export(ImagesTemplate("images.side")).Md, "| 操作前 | 操作後 |"));

        [Fact]
        public void 縦に並べる()
        {
            var r = Export(ImagesTemplate("images.vertical"));
            Assert.DoesNotContain("| 操作前 | 操作後 |", r.Md);
            Assert.Equal(4, TestSession.Count(r.Md, "**操作前:**"));
            Assert.Equal(4, TestSession.Count(r.Md, "**操作後:**"));
            Assert.DoesNotContain("<table", r.Html);
        }

        [Fact]
        public void 操作前だけ()
        {
            var r = Export(ImagesTemplate("images.before"));
            Assert.Equal(4, TestSession.Count(r.Md, "_before.png"));
            Assert.Equal(0, TestSession.Count(r.Md, "_after.png"));
            Assert.DoesNotContain("**操作前:**", r.Md);   // 見出しは付けない
        }

        [Fact]
        public void 操作後だけ()
        {
            var r = Export(ImagesTemplate("images.after"));
            Assert.Equal(0, TestSession.Count(r.Md, "_before.png"));
            Assert.Equal(4, TestSession.Count(r.Md, "_after.png"));
        }

        [Theory]
        [InlineData("images.sideMerged", "| 操作前 | 操作後 |")]
        [InlineData("images.verticalMerged", "**操作後:**")]
        public void 同じ画面なら1枚にまとめる(string variable, string twoImages)
        {
            var steps = TestSession.Steps(Export(ImagesTemplate(variable)).Md);
            // 手順 1・3 は画面が変わったので 2 枚のまま
            Assert.Contains(twoImages, steps[1]);
            Assert.Contains(twoImages, steps[3]);
            // 手順 2 は記録時に同じと判定済み、手順 4 は判定が無い古い記録なのでファイルを比べる。どちらも 1 枚
            foreach (int n in new[] { 2, 4 })
            {
                Assert.Contains("操作前・操作後(画面の変化なし)", steps[n]);
                Assert.DoesNotContain("_after.png", steps[n]);
                Assert.DoesNotContain(twoImages, steps[n]);
            }
        }

        [Fact]
        public void imagesBlockは以前のテンプレートとの互換で左右に並べる()
        {
            Assert.Equal(Export(ImagesTemplate("images.side")).Md.Replace("t_images.side", "x"),
                         Export(ImagesTemplate("imagesBlock")).Md.Replace("t_imagesBlock", "x"));
        }

        [Fact]
        public void 画像を埋め込むとファイルを参照しない()
        {
            var r = Export(embed: true);
            Assert.Equal(8, TestSession.Count(r.Html, "data:image/png;base64,"));
            Assert.DoesNotContain("img/0001", r.Md);
        }

        [Fact]
        public void 形式を選べる()
        {
            var htmlOnly = Export(md: false);
            Assert.Empty(htmlOnly.MdFiles);
            Assert.Single(htmlOnly.HtmlFiles);
            Assert.False(File.Exists(Path.ChangeExtension(htmlOnly.HtmlFiles[0], ".md")));
            var mdOnly = Export(html: false);
            Assert.Single(mdOnly.MdFiles);
            Assert.Empty(mdOnly.HtmlFiles);
        }

        [Fact]
        public void 名前と解説が見出しと冒頭に入る()
        {
            var r = Export();
            Assert.StartsWith("# 受注登録操作手順書", r.Md);
            Assert.Contains("本書では受注登録の操作手順について説明する", r.Md);
            Assert.Contains("テスト用の記録です。", r.Md);
            Assert.Contains("<title>受注登録操作手順書</title>", r.Html);
        }

        [Fact]
        public void 手順の文章の手直しが反映される()
        {
            ReportGenerator.SaveStepEdit(s.Dir, 1, "「登録」ボタンを押して確定します", "入力内容を確認してから押してください。");
            var r = Export();
            Assert.Contains("## 手順 1. 「登録」ボタンを押して確定します", r.Md);
            Assert.Contains("入力内容を確認してから押してください。", r.Html);
        }

        [Fact]
        public void ホイールの手順は向きと目盛りの数を書く()
        {
            var step3 = TestSession.Steps(Export().Md)[3];
            Assert.Contains("3 目盛り", step3);
        }

        [Theory]
        [InlineData("user")]
        [InlineData("psr")]
        [InlineData("ai")]
        public void 画像のあるテンプレートは左右に並べる(string template)
        {
            Assert.Equal(4, TestSession.Count(Export(template).Md, "| 操作前 | 操作後 |"));
        }

        [Fact]
        public void テキスト手順書は画像を載せない()
        {
            var r = Export("text");
            Assert.DoesNotContain(".png", r.Md);
            Assert.DoesNotContain("<img", r.Html);
            Assert.Equal(4, TestSession.Count(r.Md, "## 手順 "));
            Assert.Contains("## 手順 1. 「登録」ボタンをクリックします", r.Md);
            Assert.Contains("画面: 販売管理", r.Md);
            Assert.EndsWith("_text.md", r.MdFiles[0]);
        }

        [Theory]
        [InlineData("user-table")]
        [InlineData("user-before")]
        public void 以前のテンプレート名はuserとして出力する(string legacy)
        {
            var r = Export(legacy);
            Assert.EndsWith("_user.md", r.MdFiles[0]);
        }
    }
}
