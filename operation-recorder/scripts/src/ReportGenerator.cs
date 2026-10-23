using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpRec
{
    // 記録フォルダ(session.json + events.jsonl + img\)を、テンプレートに当てはめて操作報告書(md)にする。
    // New-OperationReport.ps1(スキル)と OperationRecorder アプリの両方がこのクラスを使う。
    public static class ReportGenerator
    {
        public sealed class Options
        {
            public string SessionDir;
            public IList<string> Templates = new[] { "user" };   // 名前(TemplateDir の <名前>.md)か md のパス
            public string TemplateDir;
            public string OutFile;          // テンプレートが 1 つのときだけ使える
            public string ReportDir;        // 省略時は記録フォルダの中
            public bool EmbedImages;        // true: PNG を base64 で埋め込む / false: 相対パスで参照
            public string Caption;          // null なら session.json の caption
            public string Description;      // null なら session.json の description
            // 操作前の画像に合成する印(ポインタ・丸・矢印・枠)。記録の画像には描かずに保存しているので、ここで描く。
            // 印を描いて保存していた古い記録(session.json に "marks": "output" が無い)には描かない
            public MarkOptions Marks = MarkOptions.Default();

        }

        public static List<string> Generate(Options o)
        {
            var written = new List<string>();
            foreach (var r in Render(o))
            {
                File.WriteAllText(r.Key, r.Value, new UTF8Encoding(false));
                written.Add(r.Key);
            }
            return written;
        }

        // md をファイルに書かずに作る(出力先のパスと本文の組)。アプリはこれを HTML にもする
        public static List<KeyValuePair<string, string>> Render(Options o)
        {
            string sessionDir = Path.GetFullPath(o.SessionDir);
            var session = JsonNode.Parse(File.ReadAllText(Path.Combine(sessionDir, "session.json"), Encoding.UTF8)).AsObject();
            var events = new List<JsonObject>();
            string eventsPath = Path.Combine(sessionDir, "events.jsonl");
            if (File.Exists(eventsPath))
                foreach (var line in File.ReadAllLines(eventsPath, Encoding.UTF8))
                    if (line.Trim().Length > 0) events.Add(JsonNode.Parse(line).AsObject());
            // 入力の手順はフォーカスアウトで確定するので、保存順ではなく操作した時刻の順に並べる
            events = events.OrderBy(e => Num(e["tMs"])).ThenBy(e => Num(e["seq"])).ToList();

            var templates = new List<string>();
            foreach (var t in o.Templates ?? new string[0])
                foreach (var p in t.Split(',')) if (p.Trim().Length > 0) templates.Add(p.Trim());
            if (templates.Count == 0) throw new ArgumentException("テンプレートを 1 つ以上指定してください");
            if (!string.IsNullOrEmpty(o.OutFile) && templates.Count > 1)
                throw new ArgumentException("-OutFile はテンプレートを 1 つだけ指定したときに使えます(複数のときは -ReportDir を使ってください)");

            var files = new List<string>();
            foreach (var t0 in templates)
            {
                // v1.1 までの user-table / user-before は、user に置き換わった(画像の並べ方はテンプレートの {{images.…}} で決める)。名前で指定されたら user として扱う
                string t = t0;
                if ((t == "user-table" || t == "user-before") && !File.Exists(Path.Combine(o.TemplateDir, t + ".md"))) t = "user";
                string path = (t.Contains("\\") || t.Contains("/")) ? t : Path.Combine(o.TemplateDir, t.EndsWith(".md") ? t : t + ".md");
                if (!File.Exists(path)) throw new FileNotFoundException("テンプレートが見つかりません: " + path);
                files.Add(Path.GetFullPath(path));
            }

            // 画像の相対パスは md の出力先から計算するので、出力先は先に決める
            string outDir = !string.IsNullOrEmpty(o.OutFile) ? Path.GetDirectoryName(Path.GetFullPath(o.OutFile))
                          : !string.IsNullOrEmpty(o.ReportDir) ? Path.GetFullPath(o.ReportDir)
                          : sessionDir;
            Directory.CreateDirectory(outDir);

            var model = BuildModel(session, events, sessionDir, outDir, o);

            var result = new List<KeyValuePair<string, string>>();
            string sessionName = Path.GetFileName(sessionDir.TrimEnd('\\', '/'));
            foreach (var f in files)
            {
                string text = TemplateEngine.Render(File.ReadAllText(f, Encoding.UTF8), model);
                string path = !string.IsNullOrEmpty(o.OutFile) ? Path.GetFullPath(o.OutFile)
                            : Path.Combine(outDir, sessionName + "_" + Path.GetFileNameWithoutExtension(f) + ".md");
                result.Add(new KeyValuePair<string, string>(path, Regex.Replace(text, "\r?\n", "\r\n")));
            }
            return result;
        }

        // 記録の対象の名前(session.json の targetMonitor / targetWindow / targetProcess から)。報告書の {{target}} と記録一覧で使う
        public static string TargetText(JsonObject session)
        {
            var tm = session["targetMonitor"] as JsonObject;
            var tw = session["targetWindow"] as JsonObject;
            string tp = Str(session["targetProcess"]);
            if (Str(session["mode"]) == "AllDesktops")
                return "すべてのディスプレイ(" + Raw(session["virtualScreen"]?["w"]) + "x" + Raw(session["virtualScreen"]?["h"]) + ")";
            if (tm != null) return "ディスプレイ " + Raw(tm["index"]) + "(" + Raw(tm["bounds"]?["w"]) + "x" + Raw(tm["bounds"]?["h"]) + ")";
            if (tw != null) return Str(tw["title"]);
            if (!string.IsNullOrEmpty(tp)) return tp + "(アプリ全体)";
            return "操作したウィンドウ";
        }

        // アプリの記録一覧で、手順の見出しと画像を表示するための情報
        public sealed class StepInfo
        {
            public int Number;
            public long Seq;
            public string Kind, Headline, Body, WindowTitle, BeforePath, AfterPath;
            public string TimeText, Screen, PointInWindow, ClassName, UiElements, Program, PsrText;
            public string OriginalHeadline, OriginalBody;   // 自動で作った文章(手で直す前)
            public bool Edited;                             // edits.json で直してあるか
            public MarkSpec BeforeMarks;                    // 操作前の画像に描く印の位置(古い記録は画像に描いてあるので null)
        }

        // ---------- 手順の文章の手直し(記録フォルダの edits.json) ----------
        // { "steps": { "<seq>": { "headline": "…", "body": "…" } } }
        // events.jsonl(記録そのもの)は書き換えず、md / HTML を作るときに上書きする。キーが無い項目は自動の文章のまま

        const string EditsFile = "edits.json";

        static Dictionary<long, JsonObject> LoadEdits(string sessionDir)
        {
            var d = new Dictionary<long, JsonObject>();
            string path = Path.Combine(sessionDir, EditsFile);
            if (!File.Exists(path)) return d;
            var steps = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))?["steps"] as JsonObject;
            if (steps == null) return d;
            foreach (var kv in steps)
            {
                long seq;
                var o = kv.Value as JsonObject;
                if (o != null && long.TryParse(kv.Key, out seq)) d[seq] = o;
            }
            return d;
        }

        // headline / body が null なら、その項目の手直しを消す(自動の文章に戻す)
        public static void SaveStepEdit(string sessionDir, long seq, string headline, string body)
        {
            string path = Path.Combine(sessionDir, EditsFile);
            var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject : null;
            if (root == null) root = new JsonObject();
            var steps = root["steps"] as JsonObject;
            if (steps == null) { steps = new JsonObject(); root["steps"] = steps; }
            string key = seq.ToString(CultureInfo.InvariantCulture);
            var entry = new JsonObject();
            if (headline != null) entry["headline"] = headline;
            if (body != null) entry["body"] = body;
            if (entry.Count == 0) steps.Remove(key); else steps[key] = entry;
            var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            File.WriteAllText(path, root.ToJsonString(opts), new UTF8Encoding(false));
        }

        // events.jsonl の 1 行から、自動で作る見出しを返す(アプリの撮影ログ表示用)
        public static string HeadlineFromJson(string line)
        {
            var e = JsonNode.Parse(line) as JsonObject;
            if (e == null) return "";
            string headline, body, action, inputText;
            ComposeTexts(e, out headline, out body, out action, out inputText);
            return headline;
        }

        public static List<StepInfo> ListSteps(string sessionDir)
        {
            sessionDir = Path.GetFullPath(sessionDir);
            var session = JsonNode.Parse(File.ReadAllText(Path.Combine(sessionDir, "session.json"), Encoding.UTF8)).AsObject();
            var events = new List<JsonObject>();
            string eventsPath = Path.Combine(sessionDir, "events.jsonl");
            if (File.Exists(eventsPath))
                foreach (var line in File.ReadAllLines(eventsPath, Encoding.UTF8))
                    if (line.Trim().Length > 0) events.Add(JsonNode.Parse(line).AsObject());
            events = events.OrderBy(e => Num(e["tMs"])).ThenBy(e => Num(e["seq"])).ToList();
            var model = BuildModel(session, events, sessionDir, sessionDir, new Options { Marks = MarkOptions.None() });
            var list = new List<StepInfo>();
            foreach (Dictionary<string, object> s in (List<object>)model["steps"])
            {
                var b = s["before"] as Dictionary<string, object>;
                var a = s["after"] as Dictionary<string, object>;
                list.Add(new StepInfo
                {
                    Number = (int)(long)s["number"],
                    Seq = s["seq"] is long ? (long)s["seq"] : 0,
                    Kind = s["kind"] as string,
                    Headline = s["headline"] as string,
                    Body = s["body"] as string,
                    OriginalHeadline = s["originalHeadline"] as string,
                    OriginalBody = s["originalBody"] as string,
                    Edited = s["edited"] is bool && (bool)s["edited"],
                    WindowTitle = s["windowTitle"] as string,
                    TimeText = s["timeText"] as string,
                    Screen = (s["point"] as Dictionary<string, object>)?["screen"] as string,
                    PointInWindow = (s["point"] as Dictionary<string, object>)?["window"] as string,
                    ClassName = (s["uia"] as Dictionary<string, object>)?["className"] as string,
                    UiElements = s["uiElements"] as string,
                    PsrText = s["psrText"] as string,
                    Program = ProgramText((s["window"] as Dictionary<string, object>)?["program"] as Dictionary<string, object>),
                    BeforePath = b != null ? Path.Combine(sessionDir, (string)b["file"]) : null,
                    BeforeMarks = s["beforeMarkSpec"] as MarkSpec,
                    AfterPath = a != null ? Path.Combine(sessionDir, (string)a["file"]) : null,
                });
            }
            return list;
        }

        // ---------- 文章の組み立て(New-OperationReport.ps1 から移植。出力は同じになるようにしてある) ----------

        static readonly Dictionary<string, string> TypeJa = new Dictionary<string, string>
        {
            { "Button", "ボタン" }, { "SplitButton", "ボタン" }, { "CheckBox", "チェックボックス" }, { "RadioButton", "ラジオボタン" },
            { "ComboBox", "コンボボックス" }, { "Edit", "入力欄" }, { "Document", "文書領域" }, { "Hyperlink", "リンク" },
            { "List", "一覧" }, { "ListItem", "一覧の項目" }, { "Menu", "メニュー" }, { "MenuBar", "メニューバー" }, { "MenuItem", "メニュー" },
            { "Tab", "タブ" }, { "TabItem", "タブ" }, { "Tree", "ツリー" }, { "TreeItem", "ツリーの項目" },
            { "DataGrid", "表" }, { "DataItem", "行" }, { "Table", "表" }, { "Header", "見出し" }, { "HeaderItem", "見出し" },
            { "Text", "ラベル" }, { "Image", "画像" }, { "ToolBar", "ツールバー" }, { "StatusBar", "ステータスバー" },
            { "ScrollBar", "スクロールバー" }, { "Slider", "スライダー" }, { "Spinner", "スピンボタン" },
            { "TitleBar", "タイトルバー" }, { "Window", "ウィンドウ" }, { "Calendar", "カレンダー" }, { "Group", "グループ" },
        };
        static readonly HashSet<string> GenericTypes = new HashSet<string> { "Pane", "Window", "Document", "Custom", "Group", "Text" };
        static readonly Dictionary<string, string> Verbs = new Dictionary<string, string>
        {
            { "click", "クリック" }, { "doubleclick", "ダブルクリック" }, { "rightclick", "右クリック" },
            { "middleclick", "ホイールボタンでクリック" }, { "drag", "ドラッグ" }, { "rightdrag", "右ボタンでドラッグ" },
        };

        static string Label(JsonObject uia)
        {
            if (uia == null) return "";
            string n = Regex.Replace(Str(uia["name"]) ?? "", @"\s+", " ").Trim();
            if (n.Length > 40)
            {
                if (GenericTypes.Contains(Str(uia["controlType"]) ?? "")) return "";
                n = n.Substring(0, 40) + "…";
            }
            return n;
        }

        static string TypeName(JsonObject uia)
        {
            string t;
            return uia != null && TypeJa.TryGetValue(Str(uia["controlType"]) ?? "", out t) ? t : null;
        }

        static string TargetPhrase(JsonObject uia)
        {
            if (uia == null) return "画面上の位置";
            string label = Label(uia), t = TypeName(uia);
            if (label.Length > 0 && t != null) return "「" + label + "」" + t;
            if (label.Length > 0) return "「" + label + "」";
            if (t != null) return t;
            return "画面上の位置";
        }

        static List<string> InputLines(JsonObject inp)
        {
            var r = new List<string>();
            if (inp == null) return r;
            // LabeledBy は WinForms などで無関係なラベルを返すことがあるので、要素自身の名前を優先する
            string label = new[] { Str(inp["name"]), Str(inp["label"]), Str(inp["automationId"]) }.FirstOrDefault(s => !string.IsNullOrEmpty(s));
            string field = label != null ? "「" + label + "」欄" : "入力欄";
            if (Bool(inp["isPassword"])) { r.Add(field + "にパスワードを入力します。"); return r; }
            string v = Str(inp["value"]);
            if (v == null) r.Add(field + "に入力します。");
            else if (v == "") r.Add(field + "を空にします。");
            else if (Regex.IsMatch(v, "[\r\n]") || v.Length > 60) r.AddRange(new[] { field + "に次の内容を入力します。", "", "```text", v, "```" });
            else r.Add(field + "に「" + v + "」と入力します。");
            return r;
        }

        static string ActionSentence(JsonObject e)
        {
            string kind = Str(e["kind"]) ?? "";
            var uia = e["uia"] as JsonObject;
            if (Verbs.ContainsKey(kind)) return TargetPhrase(uia) + "を" + Verbs[kind] + "します。";
            if (kind == "wheel")
            {
                var wh = e["wheel"] as JsonObject;
                long notches = (long)Num(wh?["notches"]);
                return TargetPhrase(uia) + "でマウスホイールを" + (Str(wh?["direction"]) == "up" ? "上" : "下") + "に回します" +
                       (notches > 1 ? "(" + notches + " 目盛り)" : "") + "。";
            }
            if (kind == "key")
            {
                string label = Label(uia), t = TypeName(uia);
                if (label.Length > 0 && t != null) return "「" + label + "」" + t + "で " + Str(e["key"]) + " キーを押します。";
                return Str(e["key"]) + " キーを押します。";
            }
            return null;
        }

        // marks が null でなければ、画像に印を合成する。参照(Link)なら <出力先>\<記録フォルダ名>_marked\ に書き、そこを指す
        static string ImageLink(JsonObject img, string alt, string sessionDir, string outDir, bool embed, MarkSpec marks, MarkOptions options)
        {
            string abs = Path.Combine(sessionDir, Str(img["file"]));
            if (marks != null && !marks.IsEmpty && options != null && options.Any && File.Exists(abs))
            {
                using (var bmp = new Bitmap(new MemoryStream(File.ReadAllBytes(abs))))
                using (var canvas = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(canvas))
                    {
                        g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
                        MarkPainter.Draw(g, canvas.Size, marks, options);
                    }
                    if (embed)
                        using (var ms = new MemoryStream())
                        {
                            canvas.Save(ms, ImageFormat.Png);
                            return "![" + alt + "](data:image/png;base64," + Convert.ToBase64String(ms.ToArray()) + ")";
                        }
                    string dir = Path.Combine(outDir, Path.GetFileName(sessionDir.TrimEnd('\\', '/')) + "_marked");
                    Directory.CreateDirectory(dir);
                    abs = Path.Combine(dir, Path.GetFileName(abs));
                    canvas.Save(abs, ImageFormat.Png);
                }
            }
            if (embed) return "![" + alt + "](data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(abs)) + ")";
            string rel = Path.GetRelativePath(outDir, abs).Replace('\\', '/');
            if (Regex.IsMatch(rel, @"\s")) rel = "<" + rel + ">";
            return "![" + alt + "](" + rel + ")";
        }

        static string Xy(JsonNode p) { var o = p as JsonObject; return o == null ? null : "[" + Raw(o["x"]) + ", " + Raw(o["y"]) + "]"; }
        static string Xywh(JsonNode r) { var o = r as JsonObject; return o == null ? null : "[" + Raw(o["x"]) + ", " + Raw(o["y"]) + ", " + Raw(o["w"]) + ", " + Raw(o["h"]) + "]"; }
        static string Tf(JsonNode b) { return b == null ? null : (Bool(b) ? "true" : "false"); }

        static Dictionary<string, object> ImageModel(JsonNode node, string alt, string sessionDir, string outDir, bool embed, MarkSpec marks = null, MarkOptions options = null)
        {
            var img = node as JsonObject;
            if (img == null) return null;
            return new Dictionary<string, object>
            {
                { "file", Str(img["file"]) }, { "image", ImageLink(img, alt, sessionDir, outDir, embed, marks, options) }, { "offsetMs", Value(img["offsetMs"]) },
                { "offsetBasis", Str(img["offsetBasis"]) }, { "area", Xywh(img["area"]) }, { "masked", Tf(img["masked"]) },
            };
        }

        // ---------- ステップ記録ツール(PSR)形式の情報 ----------

        static Dictionary<string, object> ProgramModel(JsonObject p)
        {
            if (p == null) return null;
            return new Dictionary<string, object>
            {
                { "name", Str(p["name"]) }, { "product", Str(p["product"]) }, { "version", Str(p["version"]) }, { "company", Str(p["company"]) },
                { "exe", Str(p["exe"]) }, { "path", Str(p["path"]) }, { "commandLine", Str(p["commandLine"]) },
            };
        }

        // 「名前, バージョン, 会社名, コマンドライン, exe」(ステップ記録ツールの Program 行と同じ並び)
        static string ProgramText(Dictionary<string, object> p)
        {
            if (p == null) return null;
            var parts = new[] { "name", "version", "company", "commandLine", "exe" }.Select(k => p[k] as string).Where(v => !string.IsNullOrEmpty(v));
            return string.Join(", ", parts);
        }

        // 操作した要素から最上位のウィンドウまでを、名前(無ければクラス名・種類)で並べる(ステップ記録ツールの UI Elements 行)
        static string UiElements(JsonObject uia)
        {
            if (uia == null) return null;
            Func<JsonObject, string> label = o =>
            {
                string n = Regex.Replace(Str(o["name"]) ?? "", @"\s+", " ").Trim();
                if (n.Length > 80) n = n.Substring(0, 80) + "…";
                return n.Length > 0 ? n : !string.IsNullOrEmpty(Str(o["className"])) ? Str(o["className"]) : Str(o["controlType"]) ?? "";
            };
            var list = new List<string> { label(uia) };
            var arr = uia["ancestors"] as JsonArray;
            if (arr != null) foreach (var a in arr) { var o = a as JsonObject; if (o != null) list.Add(label(o)); }
            return string.Join(", ", list.Where(x => x.Length > 0));
        }

        static List<string> inputLines0(JsonObject e) => InputLines(e["input"] as JsonObject);

        // ステップ記録ツールの「User left click on "X (ボタン)" in "ウィンドウ"」に当たる 1 行
        static string PsrText(JsonObject e, List<string> inputLines)
        {
            string kind = Str(e["kind"]) ?? "";
            var uia = e["uia"] as JsonObject;
            var win = e["window"] as JsonObject;
            string title = win != null ? Str(win["title"]) : null;
            string elem = "";
            if (uia != null)
            {
                string n = Regex.Replace(Str(uia["name"]) ?? "", @"\s+", " ").Trim();
                if (n.Length > 80) n = n.Substring(0, 80) + "…";
                string lt = Str(uia["localizedControlType"]);
                elem = n.Length > 0 && !string.IsNullOrEmpty(lt) ? n + " (" + lt + ")" : n.Length > 0 ? n : lt ?? "";
            }
            string op;
            if (Verbs.ContainsKey(kind)) op = (elem.Length > 0 ? "「" + elem + "」を" : "") + Verbs[kind];
            else if (kind == "wheel")
            {
                var wh = e["wheel"] as JsonObject;
                op = (elem.Length > 0 ? "「" + elem + "」で" : "") + "マウスホイールを" + (Str(wh?["direction"]) == "up" ? "上" : "下") + "に回す";
            }
            else if (kind == "key") op = (elem.Length > 0 ? "「" + elem + "」で " : "") + Str(e["key"]) + " キーを押す";
            else if (kind == "input") op = inputLines.Count > 0 ? Regex.Replace(inputLines[0], "します。$|。$", "") : "入力";
            else if (kind == "capture") op = "画面をキャプチャ" + (e["trigger"] != null ? "(" + Str(e["trigger"]) + ")" : "");
            else op = kind;
            return op + (string.IsNullOrEmpty(title) ? "" : " ― ウィンドウ「" + title + "」");
        }

        static string TimeText(string iso)
        {
            DateTime t;
            return iso != null && DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out t) ? t.ToString("yyyy/MM/dd HH:mm:ss.fff") : iso;
        }

        // ---------- 画像の並べ方 ----------

        // 操作前と操作後が同じ画面か。記録時に比べた結果(imagesSame)があればそれを使い、無い(古い記録)ときはファイルを比べる
        static bool ImagesSame(JsonObject e, string sessionDir)
        {
            var v = e["imagesSame"] as JsonValue;
            bool b;
            if (v != null && v.TryGetValue(out b)) return b;
            try
            {
                string fa = Path.Combine(sessionDir, Str(e["before"]?["file"]) ?? "");
                string fb = Path.Combine(sessionDir, Str(e["after"]?["file"]) ?? "");
                if (!File.Exists(fa) || !File.Exists(fb)) return false;
                return File.ReadAllBytes(fa).AsSpan().SequenceEqual(File.ReadAllBytes(fb));
            }
            catch { return false; }
        }

        // 画像の部分の md。layout = side / vertical / before / after、merge = 同じ画面なので 1 枚にまとめる
        static string ImagesBlock(string layout, Dictionary<string, object> before, Dictionary<string, object> after, bool merge)
        {
            string b = before?["image"] as string, a = after?["image"] as string;
            if (b == null && a == null) return "";
            layout = (layout ?? "side").ToLowerInvariant();
            // 1 枚にまとめるのは 2 枚並べる並べ方(左右・縦)のときだけ。片方だけの並べ方は、もともと 1 枚なのでそのまま
            if (merge && b != null && a != null && layout != "before" && layout != "after") return "**操作前・操作後(画面の変化なし):**\n\n" + b.Replace(" 操作前](", " 操作前・操作後](");   // 1 枚で両方を表す画像(HTML の切り替えでは、どちらの表示でも出す)
            switch (layout)
            {
                case "before":
                    return b ?? "**操作後:**\n\n" + a;
                case "after":
                    return a ?? "**操作前:**\n\n" + b;
                case "vertical":
                    {
                        var parts = new List<string>();
                        if (b != null) parts.Add("**操作前:**\n\n" + b);
                        if (a != null) parts.Add("**操作後:**\n\n" + a);
                        return string.Join("\n\n", parts);
                    }
                default:   // side: 左右に並べる(片方しか無いときは、その 1 枚だけ)
                    if (b != null && a != null) return "| 操作前 | 操作後 |\n| --- | --- |\n| " + b + " | " + a + " |";
                    return b != null ? "**操作前:**\n\n" + b : "**操作後:**\n\n" + a;
            }
        }

        // 手順の見出し・本文(自動で作る文章)
        static void ComposeTexts(JsonObject e, out string headline, out string body, out string action, out string inputText)
        {
            var inputLines = InputLines(e["input"] as JsonObject);
            action = ActionSentence(e);
            headline = action ?? (inputLines.Count > 0 ? inputLines[0] : "");   // 手動キャプチャ(Ctrl+Shift)は見出しの文を付けない
            headline = Regex.Replace(headline, "。$", "").Replace("次の内容を入力します", "入力します");
            inputText = string.Join("\n", inputLines);
            // 見出しと同じ文を本文で繰り返さない。入力を伴う手順だけ、入力 → 操作の順に本文で書く
            body = action != null && inputLines.Count > 0 ? inputText + "\n\n" + action
                 : action == null && inputLines.Count > 1 ? inputText : "";
        }

        static Dictionary<string, object> BuildModel(JsonObject session, List<JsonObject> events, string sessionDir, string outDir, Options o)
        {
            var steps = new List<object>();
            var edits = LoadEdits(sessionDir);
            int n = 0;
            foreach (var e in events)
            {
                n++;
                string headline, body, action, inputText;
                ComposeTexts(e, out headline, out body, out action, out inputText);
                string originalHeadline = headline, originalBody = body;
                JsonObject ed;
                bool edited = edits.TryGetValue((long)Num(e["seq"]), out ed);
                if (edited)
                {
                    if (ed["headline"] != null) headline = Str(ed["headline"]);
                    if (ed["body"] != null) body = Str(ed["body"]);
                }

                var win = e["window"] as JsonObject;
                var uia = e["uia"] as JsonObject;
                var w32 = e["win32"] as JsonObject;
                var inp = e["input"] as JsonObject;

                object uiaModel = null;
                if (uia != null)
                {
                    var anc = new List<object>();
                    var arr = uia["ancestors"] as JsonArray;
                    if (arr != null)
                        for (int i = arr.Count - 1; i >= 0; i--)
                        {
                            var a = arr[i] as JsonObject;
                            if (a == null) continue;
                            anc.Add(new Dictionary<string, object> { { "controlType", Str(a["controlType"]) }, { "name", Str(a["name"]) }, { "automationId", Str(a["automationId"]) }, { "className", Str(a["className"]) } });
                        }
                    uiaModel = new Dictionary<string, object>
                    {
                        { "controlType", Str(uia["controlType"]) }, { "localizedControlType", Str(uia["localizedControlType"]) }, { "name", Str(uia["name"]) }, { "automationId", Str(uia["automationId"]) },
                        { "className", Str(uia["className"]) }, { "frameworkId", Str(uia["frameworkId"]) }, { "isEnabled", Tf(uia["isEnabled"]) },
                        { "boundsInWindow", Xywh(uia["boundsInWindow"]) }, { "ancestors", anc },
                    };
                }

                // 操作前の画像に描く印(画像に印を描かずに保存した記録だけ。合成した記録の古い記録から来た手順は marksBaked)
                var beforeMarks = Str(session["marks"]) == "output" && !Bool(e["marksBaked"]) ? MarkSpec.FromEvent(e, e["before"] as JsonObject) : null;
                var beforeModel = ImageModel(e["before"], "手順 " + n + " 操作前", sessionDir, outDir, o.EmbedImages, beforeMarks, o.Marks);
                var afterModel = ImageModel(e["after"], "手順 " + n + " 操作後", sessionDir, outDir, o.EmbedImages);
                bool same = beforeModel != null && afterModel != null && ImagesSame(e, sessionDir);
                steps.Add(new Dictionary<string, object>
                {
                    { "number", (long)n }, { "headline", headline }, { "actionSentence", action }, { "inputText", inputText }, { "body", body },
                    { "originalHeadline", originalHeadline }, { "originalBody", originalBody }, { "edited", edited },
                    { "uiElements", UiElements(uia) }, { "psrText", PsrText(e, inputLines0(e)) }, { "timeText", TimeText(Str(e["time"])) },
                    { "windowTitle", win != null ? Str(win["title"]) : null },
                    { "hasImages", e["before"] is JsonObject || e["after"] is JsonObject },
                    { "imagesSame", same },
                    { "beforeMarkSpec", beforeMarks },
                    // 画像の並べ方はテンプレートが選ぶ(例: {{images.side}})。imagesBlock は以前のテンプレートとの互換用で、images.side と同じ
                    { "images", new Dictionary<string, object>
                        {
                            { "side", ImagesBlock("side", beforeModel, afterModel, false) },
                            { "vertical", ImagesBlock("vertical", beforeModel, afterModel, false) },
                            { "before", ImagesBlock("before", beforeModel, afterModel, false) },
                            { "after", ImagesBlock("after", beforeModel, afterModel, false) },
                            { "sideMerged", ImagesBlock("side", beforeModel, afterModel, same) },
                            { "verticalMerged", ImagesBlock("vertical", beforeModel, afterModel, same) },
                        } },
                    { "imagesBlock", ImagesBlock("side", beforeModel, afterModel, false) },
                    { "before", beforeModel },
                    { "after", afterModel },
                    { "seq", Value(e["seq"]) }, { "kind", Str(e["kind"]) }, { "button", Str(e["button"]) }, { "key", Str(e["key"]) }, { "time", Str(e["time"]) },
                    { "window", win == null ? null : new Dictionary<string, object> {
                        { "title", Str(win["title"]) }, { "className", Str(win["className"]) }, { "process", Str(win["processName"]) + ".exe" },
                        { "frame", Xywh(win["frame"]) }, { "hwnd", Str(win["hwnd"]) }, { "program", ProgramModel(win["program"] as JsonObject) } } },
                    { "point", e["screen"] is JsonObject ? new Dictionary<string, object> {
                        { "window", Xy(e["pointInWindow"]) }, { "client", Xy(e["pointInClient"]) }, { "screen", Xy(e["screen"]) } } : null },
                    { "dragEnd", e["dragEnd"] is JsonObject ? new Dictionary<string, object> {
                        { "window", Xy(e["dragEndInWindow"]) }, { "screen", Xy(e["dragEnd"]) } } : null },
                    { "uia", uiaModel },
                    { "win32", w32 == null ? null : new Dictionary<string, object> {
                        { "className", Str(w32["className"]) }, { "controlId", Value(w32["controlId"]) }, { "text", Str(w32["text"]) }, { "hwnd", Str(w32["hwnd"]) } } },
                    { "input", inp == null ? null : new Dictionary<string, object> {
                        { "controlType", Str(inp["controlType"]) }, { "name", Str(inp["name"]) }, { "label", Str(inp["label"]) },
                        { "automationId", Str(inp["automationId"]) }, { "isPassword", Tf(inp["isPassword"]) }, { "value", Str(inp["value"]) } } },
                });
            }

            string cap = o.Caption ?? Str(session["caption"]) ?? "";
            string desc = o.Description ?? Str(session["description"]) ?? "";
            string target = TargetText(session);

            return new Dictionary<string, object>
            {
                { "caption", cap }, { "description", desc }, { "title", cap + "操作手順書" },
                { "recordStart", (Str(session["start"]) ?? "").Replace('T', ' ') },
                { "recordEnd", (Str(session["end"]) ?? "").Replace('T', ' ') },
                { "target", target }, { "captureMode", Str(session["mode"]) }, { "command", Str(session["command"]) },
                { "stepCount", (long)steps.Count }, { "beforeMs", Value(session["beforeMs"]) }, { "afterMs", Value(session["afterMs"]) },
                { "steps", steps },
            };
        }

        // ---------- JSON の値の取り出し ----------

        static string Str(JsonNode n)
        {
            var v = n as JsonValue;
            if (v == null) return null;
            string s;
            if (v.TryGetValue(out s)) return s;
            return Raw(n);
        }

        static string Raw(JsonNode n)
        {
            if (n == null) return "";
            var v = n as JsonValue;
            if (v != null)
            {
                string s; long l; double d; bool b;
                if (v.TryGetValue(out s)) return s;
                if (v.TryGetValue(out l)) return l.ToString(CultureInfo.InvariantCulture);
                if (v.TryGetValue(out d)) return d.ToString(CultureInfo.InvariantCulture);
                if (v.TryGetValue(out b)) return b ? "True" : "False";
            }
            return n.ToJsonString();
        }

        static object Value(JsonNode n)
        {
            var v = n as JsonValue;
            if (v == null) return null;
            string s; long l; double d; bool b;
            if (v.TryGetValue(out s)) return s;
            if (v.TryGetValue(out l)) return l;
            if (v.TryGetValue(out d)) return d;
            if (v.TryGetValue(out b)) return b;
            return null;
        }

        static double Num(JsonNode n)
        {
            var v = n as JsonValue;
            double d;
            return v != null && v.TryGetValue(out d) ? d : 0;
        }

        static bool Bool(JsonNode n)
        {
            var v = n as JsonValue;
            bool b;
            if (v != null && v.TryGetValue(out b)) return b;
            string s;
            return v != null && v.TryGetValue(out s) && s.Length > 0;
        }
    }
}
