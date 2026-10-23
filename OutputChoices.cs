using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OperationRecorderApp
{
    // 「出力する報告書」で選ぶテンプレート。画面に頼らない部分だけをここに置く(画像の並べ方はテンプレートの中で決める)
    public static class OutputChoices
    {
        // ---------- ① 文章の種類(テンプレート) ----------

        public sealed class TemplateItem
        {
            public readonly string Name, Desc;
            public TemplateItem(string name, string desc) { Name = name; Desc = desc ?? ""; }
            // 一覧の表示。説明を先に出し、名前(ファイル名)は後ろに添える(例: ユーザー向けの手順書（操作前後画像）[user])
            public override string ToString() => (Desc.Length > 0 ? StripOuterParens(Desc) : Name) + "[" + Name + "]";
        }

        // 説明を囲む外側の括弧 1 組だけを外す(中の「（操作前後画像）」のような括弧は残す)
        public static string StripOuterParens(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length >= 2 && (s[0] == '(' || s[0] == '（') && (s[s.Length - 1] == ')' || s[s.Length - 1] == '）'))
            {
                // 先頭の括弧が末尾の括弧と対になっているときだけ外す
                int depth = 0;
                for (int i = 0; i < s.Length; i++)
                {
                    if (s[i] == '(' || s[i] == '（') depth++;
                    else if (s[i] == ')' || s[i] == '）') { depth--; if (depth == 0 && i < s.Length - 1) return s; }
                }
                return s.Substring(1, s.Length - 2).Trim();
            }
            return s;
        }

        // テンプレート冒頭コメントの 3 行目から説明を取り出す(PromptHelpers.ps1 の Get-TemplateItems と同じ規則)
        public static string DescriptionFromLine(string line3) =>
            Regex.Replace((line3 ?? "").Trim(), @"^操作報告書テンプレート|\s+-Template\s+\S+\s*$", "").Trim();

        // フォルダの *.md を一覧にする。いちばんよく使う user を先頭に、あとは名前順
        public static List<TemplateItem> LoadTemplates(string dir)
        {
            var list = new List<TemplateItem>();
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.GetFiles(dir, "*.md").OrderBy(f => Path.GetFileNameWithoutExtension(f) == "user" ? 0 : 1)
                                                            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string desc = "";
                try
                {
                    var lines = File.ReadLines(f, Encoding.UTF8).Take(3).ToList();
                    if (lines.Count >= 3) desc = DescriptionFromLine(lines[2]);
                }
                catch { }
                list.Add(new TemplateItem(Path.GetFileNameWithoutExtension(f), desc));
            }
            return list;
        }
    }
}
