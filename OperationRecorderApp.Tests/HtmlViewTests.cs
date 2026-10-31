using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace OperationRecorderApp.Tests
{
    // HTMLの切替・クリック拡大・Esc補助。任意のCLI用成果物も同じ生成経路から書き出す。
    public class HtmlViewTests : IDisposable
    {
        readonly TestSession s = new TestSession();
        public void Dispose() => s.Dispose();

        static string TemplateDir => Path.Combine(AppContext.BaseDirectory, "templates");

        string Html(string template = "user", bool embed = false)
        {
            var htmls = new List<string>();
            RecordingController.Export(TemplateDir, s.Dir, new[] { template }, null, embed, false, true, null, null, new List<string>(), htmls);
            return File.ReadAllText(htmls[0]);
        }

        // 画像の並べ方だけを変えたテンプレート
        string ImagesTemplate(string variable)
        {
            string dir = Path.Combine(s.Root, "tpl");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "t_" + variable + ".md");
            File.WriteAllText(path, "# 手順書\n\n{{#each steps}}\n## 手順 {{number}}. {{headline}}\n\n{{" + variable + "}}\n\n{{#endeach}}\n");
            return path;
        }

        [Fact]
        public void 切り替えのボタンは最初の手順の直前に置く()
        {
            string h = Html();
            int title = h.IndexOf("<h1", StringComparison.Ordinal);
            int intro = h.IndexOf("本書では受注登録の操作手順について説明する", StringComparison.Ordinal);
            int radio = h.IndexOf("id=\"view-before\"", StringComparison.Ordinal);
            int report = h.IndexOf("<div class=\"report\">", StringComparison.Ordinal);
            int step1 = h.IndexOf("手順 1.", report, StringComparison.Ordinal);
            Assert.True(title < intro && intro < radio, "冒頭(題名・解説)より後ろに無い");
            Assert.True(radio < report && report < step1, "最初の手順の直前に無い(兄弟セレクタが効かない)");
            Assert.Equal(report + "<div class=\"report\">\n".Length, h.IndexOf("<h2", report, StringComparison.Ordinal));   // 手順の部分は見出しから始まる
            string shown = h.Substring(h.IndexOf("<body>", StringComparison.Ordinal));   // 画面に出る部分(CSS のコメントは除く)
            Assert.DoesNotContain("画像:", shown);
            Assert.DoesNotContain("クリックで拡大", shown);
            Assert.Contains("<label for=\"view-before\">操作前だけ</label>", h);
            Assert.Contains("<label for=\"view-after\">操作後だけ</label>", h);
            Assert.Contains("id=\"view-both\" class=\"view-radio\" checked", h);   // 最初は両方を出す
            Assert.Contains("#view-before:checked ~ .report .shot-after", h);
            Assert.Contains("#view-after:checked ~ .report .shot-before", h);
            Assert.Contains("event.key !== 'Escape'", h);
            Artifact("both", Html(embed: true));
        }

        [Fact]
        public void 左右に並べた表の列に印を付ける()
        {
            string h = Html();
            Assert.Equal(4, TestSession.Count(h, "<th class=\"shot-before\">操作前</th>"));
            Assert.Equal(4, TestSession.Count(h, "<th class=\"shot-after\">操作後</th>"));
            Assert.Equal(4, TestSession.Count(h, "<td class=\"shot-before\">"));
            Assert.Equal(4, TestSession.Count(h, "<td class=\"shot-after\">"));
        }

        [Fact]
        public void 縦に並べたときは見出しと画像に印を付ける()
        {
            string h = Html(ImagesTemplate("images.vertical"));
            Assert.Equal(4, TestSession.Count(h, "<p class=\"shot-before\"><strong>操作前:</strong></p>"));
            Assert.Equal(4, TestSession.Count(h, "<p class=\"shot-after\"><strong>操作後:</strong></p>"));
            Assert.Equal(8, Regex.Matches(h, "<p class=\"shot-(before|after)\"><span class=\"zoom\"").Count);
        }

        [Fact]
        public void 一枚にまとめた画像はどちらの表示でも出す()
        {
            string h = Html(ImagesTemplate("images.sideMerged"));
            // 手順 2・4 は画面が変わっていないので 1 枚。その画像には印を付けない(隠れない)
            Assert.Equal(2, TestSession.Count(h, "<td class=\"shot-after\">"));
            Assert.Equal(2, TestSession.Count(h, "alt=\"手順 2 操作前・操作後\"") + TestSession.Count(h, "alt=\"手順 4 操作前・操作後\""));
            Assert.DoesNotContain("<p class=\"shot-before\"><span class=\"zoom\" id=\"zoom-3\"", h);
        }

        [Fact]
        public void 画像はクリックで拡大できる()
        {
            string h = Html();
            for (int k = 1; k <= 8; k++)
            {
                Assert.Contains("<span class=\"zoom\" id=\"zoom-" + k + "\"><a class=\"zoom-open\" href=\"#zoom-" + k + "\"><img ", h);
            }
            Assert.Equal(8, TestSession.Count(h, "<a class=\"zoom-close\" href=\"#_\""));
            Assert.Equal(8, TestSession.Count(h, ">閉じる（ESC）</a>"));
            Assert.Contains("window.location.hash = '_'", h);
            Assert.Contains(".zoom:target", h);
        }

        [Fact]
        public void 埋め込んでも画像は増えない()
        {
            string h = Html(embed: true);
            Assert.Equal(8, TestSession.Count(h, "data:image/png;base64,"));   // 拡大は同じ画像を広げるだけ
        }

        [Fact]
        public void 印刷では切り替えを隠して両方を出す()
        {
            string css = Regex.Match(Html(), @"@media print \{.*?\n\}", RegexOptions.Singleline).Value;
            Assert.Contains(".view-switch", css);
            Assert.Contains(".report .shot-before, .report .shot-after { display: revert !important; }", css);
        }

        [Fact]
        public void 画像の無い手順書には切り替えを出さない()
        {
            string h = Html("text");
            Assert.DoesNotContain("id=\"view-before\"", h);
            Assert.DoesNotContain("class=\"zoom\"", h);
            Assert.DoesNotContain("<script", h);
        }

        [Theory]
        [InlineData("before")]
        [InlineData("after")]
        public void 片側だけの全手順には切り替えを出さない(string side)
        {
            string html = Html(ImagesTemplate("images." + side), embed: true);
            Assert.Equal(4, TestSession.Count(html, "<img "));
            for (int n = 1; n <= 4; n++) Assert.Contains("alt=\"手順 " + n + " 操作" + (side == "before" ? "前" : "後") + "\"", html);
            Assert.DoesNotContain("id=\"view-both\"", html);
            Assert.DoesNotContain("<div class=\"view-switch\"", html);
            Artifact(side, html);
        }

        [Fact]
        public void 別々の手順に片側ずつあるだけでは切り替えを出さない()
        {
            const string markdown = "# 手順書\n\n## 手順 1\n\n![手順 1 操作前](before.png)\n\n## 手順 2\n\n![手順 2 操作後](after.png)\n";
            var html = HtmlExporter.ToHtml(markdown);
            Assert.DoesNotContain("id=\"view-both\"", html);
            Assert.Equal(2, TestSession.Count(html, "<img "));
        }

        static void Artifact(string name, string html)
        {
            var directory = Environment.GetEnvironmentVariable("OPREC_HTML_TEST_ARTIFACTS");
            if (string.IsNullOrWhiteSpace(directory)) return;
            directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, name + ".html"), html);
        }
    }
}
