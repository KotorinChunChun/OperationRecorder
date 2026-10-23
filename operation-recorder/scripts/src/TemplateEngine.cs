using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpRec
{
    // 報告書テンプレートの小さなエンジン。
    //   {{var}} / {{q var}}(JSON 形式の引用付き。null は null)
    //   {{#if var}} … {{#else}} … {{#endif}} / {{#unless var}} … {{#endunless}} / {{#each list}} … {{#endeach}}
    //   {{!-- コメント --}}
    // 制御タグはすべて # で始める(ユーザーの指定)。制御タグやコメントだけの行は行ごと消す。
    // データは Dictionary<string, object> / IList / string / long / double / bool / null で渡す。
    public static class TemplateEngine
    {
        sealed class Node
        {
            public string T;                    // text / var / section / root
            public string V, Path, Kind;
            public bool Quote, InElse;
            public List<Node> Children = new List<Node>(), Else = new List<Node>();
        }

        static readonly JsonSerializerOptions QuoteJson = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public static string Render(string template, object model)
        {
            var root = Parse(template.Replace("\r\n", "\n"));
            var sb = new StringBuilder();
            var scopes = new List<object> { model };
            Run(root.Children, scopes, sb);
            return sb.ToString();
        }

        static Node Parse(string src)
        {
            src = Regex.Replace(src, @"(?ms)^[ \t]*\{\{!--.*?--\}\}[ \t]*\r?\n", "");
            src = Regex.Replace(src, @"(?s)\{\{!--.*?--\}\}", "");
            src = Regex.Replace(src, @"(?m)^[ \t]*(\{\{\s*#[^}]*\}\})[ \t]*\r?\n", "$1");

            var root = new Node { T = "root", Kind = "root" };
            var stack = new Stack<Node>();
            stack.Push(root);
            int pos = 0;
            foreach (Match m in Regex.Matches(src, @"\{\{\s*(.*?)\s*\}\}"))
            {
                var top = stack.Peek();
                var target = top.InElse ? top.Else : top.Children;
                if (m.Index > pos) target.Add(new Node { T = "text", V = src.Substring(pos, m.Index - pos) });
                pos = m.Index + m.Length;
                string tag = m.Groups[1].Value;

                if (tag.StartsWith("#"))
                {
                    var parts = Regex.Split(tag.Substring(1).Trim(), @"\s+");
                    string cmd = parts[0];
                    if (cmd == "if" || cmd == "unless" || cmd == "each")
                    {
                        if (parts.Length < 2) throw new FormatException("テンプレートの {{#" + cmd + "}} に変数名がありません");
                        var node = new Node { T = "section", Kind = cmd, Path = string.Join(" ", parts, 1, parts.Length - 1) };
                        target.Add(node);
                        stack.Push(node);
                    }
                    else if (cmd == "else")
                    {
                        if (top.Kind != "if" && top.Kind != "unless") throw new FormatException("テンプレートの {{#else}} は {{#if}} / {{#unless}} の中でだけ使えます");
                        if (top.InElse) throw new FormatException("テンプレートの {{#" + top.Kind + " " + top.Path + "}} に {{#else}} が 2 つあります");
                        top.InElse = true;
                    }
                    else if (cmd == "endif" || cmd == "endunless" || cmd == "endeach")
                    {
                        string kind = cmd.Substring(3);
                        if (stack.Count <= 1) throw new FormatException("テンプレートの {{#" + cmd + "}} に対応する {{#" + kind + "}} がありません");
                        if (top.Kind != kind) throw new FormatException("テンプレートの {{#" + top.Kind + " " + top.Path + "}} を {{#" + cmd + "}} で閉じています({{#end" + top.Kind + "}} が必要)");
                        stack.Pop();
                    }
                    else throw new FormatException("テンプレートに不明な制御タグがあります: {{" + tag + "}}(使えるのは #if #unless #each #else #endif #endunless #endeach)");
                }
                else if (tag.StartsWith("/") || tag == "else")
                {
                    throw new FormatException("テンプレートの {{" + tag + "}} は古い書き方です。{{#else}} / {{#endif}} / {{#endunless}} / {{#endeach}} を使ってください");
                }
                else
                {
                    var parts = Regex.Split(tag, @"\s+");
                    if (parts.Length == 2 && parts[0] == "q") target.Add(new Node { T = "var", Path = parts[1], Quote = true });
                    else target.Add(new Node { T = "var", Path = tag, Quote = false });
                }
            }
            if (stack.Count > 1)
            {
                var open = stack.Peek();
                throw new FormatException("テンプレートの {{#" + open.Kind + " " + open.Path + "}} が {{#end" + open.Kind + "}} で閉じられていません");
            }
            if (pos < src.Length) stack.Peek().Children.Add(new Node { T = "text", V = src.Substring(pos) });
            return root;
        }

        static void Run(List<Node> nodes, List<object> scopes, StringBuilder sb)
        {
            foreach (var n in nodes)
            {
                switch (n.T)
                {
                    case "text": sb.Append(n.V); break;
                    case "var":
                        {
                            object v = Resolve(n.Path, scopes);
                            if (n.Quote) sb.Append(Quote(v));
                            else if (v != null) sb.Append(ToText(v));
                            break;
                        }
                    case "section":
                        {
                            object v = Resolve(n.Path, scopes);
                            if (n.Kind == "if") Run(Truthy(v) ? n.Children : n.Else, scopes, sb);
                            else if (n.Kind == "unless") Run(Truthy(v) ? n.Else : n.Children, scopes, sb);
                            else if (Truthy(v))
                            {
                                foreach (var item in AsList(v))
                                {
                                    scopes.Add(item);
                                    Run(n.Children, scopes, sb);
                                    scopes.RemoveAt(scopes.Count - 1);
                                }
                            }
                            break;
                        }
                }
            }
        }

        // 内側のスコープから順に、先頭の名前を持っているものを探す(値が null でも見つかった時点で確定)
        static object Resolve(string path, List<object> scopes)
        {
            if (path == "this") return scopes[scopes.Count - 1];
            var segs = path.Split('.');
            for (int i = scopes.Count - 1; i >= 0; i--)
            {
                bool found;
                object v = Member(scopes[i], segs[0], out found);
                if (!found) continue;
                for (int j = 1; j < segs.Length; j++) v = Member(v, segs[j], out found);
                return v;
            }
            return null;
        }

        static object Member(object obj, string name, out bool found)
        {
            found = false;
            var d = obj as IDictionary<string, object>;
            if (d == null) return null;
            object v;
            found = d.TryGetValue(name, out v);
            return v;
        }

        static bool Truthy(object v)
        {
            if (v == null) return false;
            if (v is bool) return (bool)v;
            var s = v as string;
            if (s != null) return s.Length > 0;
            if (v is IDictionary<string, object>) return true;
            var e = v as IEnumerable;
            if (e != null) { foreach (var _ in e) return true; return false; }
            return true;
        }

        static IEnumerable AsList(object v)
        {
            if (v is IEnumerable && !(v is string) && !(v is IDictionary<string, object>)) return (IEnumerable)v;
            return new[] { v };
        }

        static string ToText(object v)
        {
            if (v is bool) return (bool)v ? "True" : "False";   // pwsh の [string]$true と同じ
            if (v is IFormattable) return ((IFormattable)v).ToString(null, CultureInfo.InvariantCulture);
            return v.ToString();
        }

        public static string Quote(object v)
        {
            if (v == null) return "null";
            if (v is bool) return (bool)v ? "true" : "false";
            if (v is int || v is long || v is double) return ToText(v);
            return JsonSerializer.Serialize(ToText(v), QuoteJson);
        }
    }
}
