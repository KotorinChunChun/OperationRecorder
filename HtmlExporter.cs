using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;

namespace OperationRecorderApp
{
    // 操作手順書(md)を、ブラウザで読める HTML にする。画像のパスは md と同じ(md と同じフォルダに HTML を置く前提)。
    // JavaScript は使わず、CSS だけで次のことをする(メールのプレビューなど CSS が効かない所では、今までどおり両方の画像が並ぶ)
    //   ・操作前 / 操作後だけの表示切り替え(最初の手順の直前に置くラジオボタン + :checked)。印刷するときは切り替えを隠して両方を出す
    //   ・画像をクリックすると画面いっぱいに拡大(:target)。もう一度クリックするか「戻る」で元に戻る
    public static class HtmlExporter
    {
        static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

        const string Css = @"
body { font-family: 'Yu Gothic UI', 'Meiryo', sans-serif; max-width: 1100px; margin: 2em auto; padding: 0 1.2em; line-height: 1.75; color: #222; }
h1 { color: #1e3c78; border-bottom: 3px solid #1e3c78; padding-bottom: .3em; }
h2 { color: #1e3c78; border-bottom: 1px solid #b8c4dc; padding-bottom: .2em; margin-top: 2.2em; }
img { max-width: 100%; border: 1px solid #ccc; box-shadow: 0 1px 4px rgba(0,0,0,.12); }
table { border-collapse: collapse; margin: .8em 0; }
th, td { border: 1px solid #ccc; padding: 6px 10px; vertical-align: top; }
th { background: #eef2f8; }
td img { max-width: 100%; }
table.images { width: 100%; table-layout: fixed; }
table.images th { text-align: left; }
code { background: #f3f3f3; padding: 0 .3em; border-radius: 3px; }
pre { background: #f6f6f6; padding: .8em 1em; overflow-x: auto; border-radius: 4px; }
pre code { background: none; padding: 0; }

/* 操作前 / 操作後の表示切り替え */
.view-radio { position: absolute; opacity: 0; pointer-events: none; }
.view-switch { position: sticky; top: 0; z-index: 10; background: #fff; border-bottom: 1px solid #d8dee9; padding: .4em 0; margin-bottom: .6em; font-size: .92em; }
.view-switch label { display: inline-block; padding: .15em .9em; margin-right: .3em; border: 1px solid #b8c4dc; border-radius: 1em; cursor: pointer; color: #1e3c78; }
#view-both:checked ~ .view-switch label[for=view-both],
#view-before:checked ~ .view-switch label[for=view-before],
#view-after:checked ~ .view-switch label[for=view-after] { background: #1e3c78; color: #fff; }
#view-before:checked ~ .report .shot-after,
#view-after:checked ~ .report .shot-before { display: none; }

/* クリックで拡大 */
.zoom { display: inline-block; }
.zoom > .zoom-open { cursor: zoom-in; }
.zoom > .zoom-close { display: none; }
.zoom:target { position: fixed; top: 0; left: 0; right: 0; bottom: 0; z-index: 100; background: rgba(0,0,0,.85); text-align: center; }
.zoom:target > .zoom-open img { position: absolute; top: 50%; left: 50%; transform: translate(-50%, -50%); max-width: 96vw; max-height: 94vh; width: auto; height: auto; border: none; }
.zoom:target > .zoom-close { display: block; position: fixed; top: 0; left: 0; right: 0; bottom: 0; z-index: 101; cursor: zoom-out; }

@media print {
  body { max-width: none; margin: 0; }
  h2 { page-break-after: avoid; }
  img { page-break-inside: avoid; }
  .view-switch, .zoom > .zoom-close { display: none !important; }
  .report .shot-before, .report .shot-after { display: revert !important; }
}
";

        // 切り替えのラジオボタンと、そのラベル。最初の手順の直前に置き、兄弟セレクタ ~ で後ろの手順(.report)に効かせる
        // (:has() を使わないので古いブラウザでも動く)
        const string ViewSwitch =
            "<input type=\"radio\" name=\"view\" id=\"view-both\" class=\"view-radio\" checked>\n" +
            "<input type=\"radio\" name=\"view\" id=\"view-before\" class=\"view-radio\">\n" +
            "<input type=\"radio\" name=\"view\" id=\"view-after\" class=\"view-radio\">\n" +
            "<div class=\"view-switch\"><label for=\"view-both\">操作前と操作後</label><label for=\"view-before\">操作前だけ</label><label for=\"view-after\">操作後だけ</label></div>\n";

        public static string ToHtml(string markdown)
        {
            string body = Markdown.ToHtml(markdown, Pipeline);
            // 画像の入った表(操作前 | 操作後)は横幅いっぱいに広げる。:has() が使えないブラウザ(エディタのプレビュー)でも効くよう、クラスを付ける
            body = Regex.Replace(body, @"<table>(?=(?:(?!</table>).)*?<img)", "<table class=\"images\">", RegexOptions.Singleline);
            bool switchable = MarkBeforeAfter(ref body);
            body = WrapZoom(body);
            var m = Regex.Match(markdown, @"^#\s+(.+)$", RegexOptions.Multiline);
            string title = m.Success ? m.Groups[1].Value.Trim() : "操作手順書";
            // 冒頭(題名・解説など)と手順の部分に分け、その間に切り替えを置く
            string intro = "", steps = body;
            if (switchable)
            {
                int at = StepsStart(body);
                intro = body.Substring(0, at);
                steps = body.Substring(at);
            }
            return "<!DOCTYPE html>\n<html lang=\"ja\">\n<head>\n<meta charset=\"utf-8\">\n<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n" +
                   "<title>" + WebUtility.HtmlEncode(title) + "</title>\n<style>" + Css + "</style>\n</head>\n<body>\n" +
                   (intro.Length > 0 ? "<div class=\"intro\">\n" + intro + "</div>\n" : "") +
                   (switchable ? ViewSwitch : "") + "<div class=\"report\">\n" + steps + "</div>\n</body>\n</html>\n";
        }

        // 手順の部分が始まる位置: 最初に操作前 / 操作後の画像が出てくる手順の見出し(その前の最後の <h2>)。見出しが無ければその画像の段落・表
        static int StepsStart(string html)
        {
            int shot = html.IndexOf("class=\"shot-", System.StringComparison.Ordinal);
            if (shot < 0) return 0;
            int h2 = html.LastIndexOf("<h2", shot, System.StringComparison.Ordinal);
            if (h2 >= 0) return h2;
            int block = System.Math.Max(html.LastIndexOf("<p", shot, System.StringComparison.Ordinal), html.LastIndexOf("<table", shot, System.StringComparison.Ordinal));
            return System.Math.Max(0, block);
        }

        // 操作前・操作後の画像(alt が「手順 N 操作前」「手順 N 操作後」)と、その見出し(「操作前:」「操作後:」)・表の列に、
        // shot-before / shot-after を付ける。「画面の変化なし」で 1 枚にまとめた画像は、どちらの表示でも出す。付けたものがあれば true
        static bool MarkBeforeAfter(ref string html)
        {
            int n = 0;
            string Side(string s) => s == "前" ? "shot-before" : "shot-after";
            // 画像だけが入った段落・表のセル
            html = Regex.Replace(html, @"<(p|td)>(\s*<img[^>]*\balt=""手順 \d+ 操作(前|後)""[^>]*>\s*)</\1>",
                mm => { n++; return "<" + mm.Groups[1].Value + " class=\"" + Side(mm.Groups[3].Value) + "\">" + mm.Groups[2].Value + "</" + mm.Groups[1].Value + ">"; });
            // 縦に並べたときの見出し「操作前:」「操作後:」
            html = Regex.Replace(html, @"<p><strong>操作(前|後):</strong></p>", mm => "<p class=\"" + Side(mm.Groups[1].Value) + "\"><strong>操作" + mm.Groups[1].Value + ":</strong></p>");
            // 左右に並べた表の見出しの列
            html = Regex.Replace(html, @"<th>操作(前|後)</th>", mm => "<th class=\"" + Side(mm.Groups[1].Value) + "\">操作" + mm.Groups[1].Value + "</th>");
            return n > 0;
        }

        // 画像を、クリックで拡大できる入れ物で包む(同じ画像を拡大して見せるので、埋め込んだ画像でもファイルは大きくならない)
        static string WrapZoom(string html)
        {
            int k = 0;
            return Regex.Replace(html, @"<img\b[^>]*>", mm =>
            {
                k++;
                string id = "zoom-" + k;
                return "<span class=\"zoom\" id=\"" + id + "\"><a class=\"zoom-open\" href=\"#" + id + "\">" + mm.Value + "</a>" +
                       "<a class=\"zoom-close\" href=\"#_\" title=\"閉じる\" aria-label=\"閉じる\"></a></span>";
            });
        }

        // md のパス(拡張子 .md)と同じ場所・同じ名前で .html を書き、そのパスを返す
        public static string Write(string mdPath, string markdown)
        {
            string path = Path.ChangeExtension(mdPath, ".html");
            File.WriteAllText(path, ToHtml(markdown), new UTF8Encoding(false));
            return path;
        }
    }
}
