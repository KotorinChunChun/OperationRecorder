using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpRec;

namespace OperationRecorderApp
{
    // テンプレート(templates フォルダの *.md)の追加・削除・保存・プレビュー。テンプレートのエディタの画面から呼ぶ。
    // 画面に頼らないので、自動テストから直接確かめられる
    public static class TemplateStore
    {
        public static string PathOf(string dir, string name) => Path.Combine(dir, name + ".md");

        public static List<string> Names(string dir) => OutputChoices.LoadTemplates(dir).Select(t => t.Name).ToList();

        public static string Read(string dir, string name) => File.ReadAllText(PathOf(dir, name), Encoding.UTF8);

        // 新しいテンプレートの名前として使えるか(使えなければ理由を返す)
        public static string CheckNewName(string dir, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return "名前を入れてください";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(",") || name.EndsWith(".") || name.Length > 60)
                return "名前に使えない文字があります(\\ / : * ? \" < > | , と、末尾の . は使えません。60 文字まで)";
            if (File.Exists(PathOf(dir, name))) return "「" + name + "」はもうあります";
            return null;
        }

        // 書き方の誤り(タグの対応が取れていないなど)。正しければ null
        public static string Check(string text)
        {
            try { TemplateEngine.Render(text ?? "", new Dictionary<string, object>()); return null; }
            catch (Exception ex) { return ex.Message; }
        }

        // 新しく作る(source の中身をもとにする。null なら空の骨組み)
        public static void Create(string dir, string name, string sourceText)
        {
            string err = CheckNewName(dir, name);
            if (err != null) throw new ArgumentException(err);
            Directory.CreateDirectory(dir);
            File.WriteAllText(PathOf(dir, name.Trim()), sourceText ?? Skeleton(name.Trim()), new UTF8Encoding(false));
        }

        public static void Save(string dir, string name, string text)
        {
            string err = Check(text);
            if (err != null) throw new InvalidOperationException(err);
            File.WriteAllText(PathOf(dir, name), text, new UTF8Encoding(false));
        }

        // toRecycleBin = true ならごみ箱へ(元に戻せる)。テストでは false
        public static void Delete(string dir, string name, bool toRecycleBin)
        {
            string p = PathOf(dir, name);
            if (!File.Exists(p)) return;
            if (toRecycleBin) RecycleBin.SendFile(p);
            else File.Delete(p);
        }

        // 保存していない中身のまま、記録フォルダに当てはめた md を作る(画像は埋め込むので、どこに置いても表示できる)
        public static string Preview(string sessionDir, string text)
        {
            string err = Check(text);
            if (err != null) throw new InvalidOperationException(err);
            string tmp = Path.Combine(Path.GetTempPath(), "OperationRecorder-template-preview");
            Directory.CreateDirectory(tmp);
            string tpl = Path.Combine(tmp, "preview.md");
            File.WriteAllText(tpl, text, new UTF8Encoding(false));
            var r = ReportGenerator.Render(new ReportGenerator.Options
            {
                SessionDir = sessionDir,
                Templates = new[] { tpl },
                TemplateDir = tmp,
                ReportDir = tmp,
                EmbedImages = true,

            });
            return r[0].Value;
        }

        static string Skeleton(string name) =>
            "{{!--\n" +
            "===============================================================================\n" +
            " 操作報告書テンプレート(" + name + " の説明をここに書く)  -Template " + name + "\n" +
            "===============================================================================\n" +
            " 使える変数は user.md / ai.md の冒頭コメントを参照\n" +
            "--}}\n" +
            "# {{caption}}操作手順書\n\n" +
            "{{#each steps}}\n" +
            "## 手順 {{number}}.{{#if headline}} {{headline}}{{#endif}}\n\n" +
            "{{#if images.side}}\n{{images.side}}\n\n{{#endif}}\n" +
            "{{#endeach}}\n";
    }
}
