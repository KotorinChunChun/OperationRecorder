using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace OpRec
{
    // 2 つの記録(前の記録 A と、取り直した記録 B)を 1 つにまとめる。
    // 手順は「どの画面の、どの部品に、どんな操作をしたか」(ウィンドウのクラス名・部品の種類 / ID / クラス名・キーなど)で
    // 突き合わせる。クリック位置や入力した文字は毎回違うことがあるので、突き合わせには使わず、重なった手順でどちらを採るかを選ぶ。
    public static class SessionMerger
    {
        public enum RowKind { OnlyA, OnlyB, Both }

        // まとめた結果の 1 行
        public sealed class Row
        {
            public RowKind Kind;
            public int A = -1, B = -1;     // 手順の位置(0 から)。無い側は -1
            public bool Include;           // まとめた記録に入れるか
            public bool UseA;              // Both のとき、A(前の記録)の手順を採るか(false なら B)
            public override string ToString() => Kind + " A=" + A + " B=" + B + (Include ? "" : " (入れない)") + (Kind == RowKind.Both ? (UseA ? " A を採る" : " B を採る") : "");
        }

        // 突き合わせに使う、手順の「同じ操作か」の鍵。座標・入力した文字・日時は含めない
        public static string Signature(JsonObject e)
        {
            if (e == null) return "";
            string kind = S(e["kind"]);
            var parts = new List<string> { kind, S(e["button"]), S(e["window"]?["className"]) };
            var uia = e["uia"] as JsonObject;
            if (uia != null)
            {
                string id = S(uia["automationId"]);
                parts.Add(S(uia["controlType"]));
                parts.Add(id);
                parts.Add(S(uia["className"]));
                if (id.Length == 0) parts.Add(S(uia["name"]));   // ID の無い部品は名前で見分ける
            }
            if (kind == "key") parts.Add(S(e["key"]));
            if (kind == "input")
            {
                var inp = e["input"] as JsonObject;
                parts.Add(S(inp?["automationId"]));
                parts.Add(S(inp?["name"]));
            }
            if (kind == "wheel") parts.Add(S(e["wheel"]?["direction"]));
            return string.Join("|", parts);
        }

        static string S(JsonNode n)
        {
            try { return n == null ? "" : n.ToString(); } catch { return ""; }
        }

        // 突き合わせて、まとめ方の案を作る。preferA = true なら、重なった手順は前の記録 A を採る
        //   ・最初に重なった手順より前の A の手順は残す(取り直す前の部分)
        //   ・最初から最後に重なった手順までの間は B を採る(A だけの手順は、やり直した部分なので入れない)
        //   ・最後に重なった手順の後: B に続きがあれば B を採り A の残りは入れない。B が重なった手順で終わっていれば A の残りを入れる
        public static List<Row> Plan(IList<JsonObject> a, IList<JsonObject> b, bool preferA)
        {
            var sa = a.Select(Signature).ToArray();
            var sb = b.Select(Signature).ToArray();
            int n = sa.Length, m = sb.Length;
            // 最長共通部分列(LCS)で突き合わせる
            var len = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    len[i, j] = sa[i] == sb[j] ? len[i + 1, j + 1] + 1 : Math.Max(len[i + 1, j], len[i, j + 1]);
            var rows = new List<Row>();
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && sa[x] == sb[y]) { rows.Add(new Row { Kind = RowKind.Both, A = x++, B = y++ }); continue; }
                // 同じ隙間では、A だけの手順を先に並べる
                if (x < n && (y >= m || len[x + 1, y] >= len[x, y + 1])) rows.Add(new Row { Kind = RowKind.OnlyA, A = x++ });
                else rows.Add(new Row { Kind = RowKind.OnlyB, B = y++ });
            }

            int first = rows.FindIndex(r => r.Kind == RowKind.Both);
            int last = rows.FindLastIndex(r => r.Kind == RowKind.Both);
            bool bContinues = last >= 0 && rows.Skip(last + 1).Any(r => r.Kind == RowKind.OnlyB);
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                r.UseA = preferA;
                if (first < 0) { r.Include = true; continue; }               // 1 つも重ならない: 両方そのまま(A の後に B)
                if (r.Kind == RowKind.Both || r.Kind == RowKind.OnlyB) r.Include = true;
                else if (i < first) r.Include = true;                         // 取り直す前の A
                else if (i > last) r.Include = !bContinues;                   // A の残り
                else r.Include = false;                                       // やり直した部分の A
            }
            if (first < 0)
            {
                // 重ならないときは A をすべて、その後に B をすべて
                rows = rows.Where(r => r.Kind == RowKind.OnlyA).Concat(rows.Where(r => r.Kind == RowKind.OnlyB)).ToList();
            }
            return rows;
        }

        public static List<JsonObject> LoadEvents(string sessionDir)
        {
            string path = Path.Combine(sessionDir, "events.jsonl");
            var list = new List<JsonObject>();
            if (File.Exists(path))
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                    if (line.Trim().Length > 0) list.Add(JsonNode.Parse(line).AsObject());
            // 報告書と同じ順(操作した時刻の順)
            return list.OrderBy(e => Num(e["tMs"])).ThenBy(e => Num(e["seq"])).ToList();
        }

        static double Num(JsonNode n)
        {
            try { return n == null ? 0 : n.GetValue<double>(); } catch { return 0; }
        }

        // 案のとおりにまとめて、新しい記録フォルダを outRoot の中に作る。作ったフォルダのパスを返す
        public static string Merge(string dirA, string dirB, IList<Row> plan, string outRoot)
        {
            var a = LoadEvents(dirA);
            var b = LoadEvents(dirB);
            var sessA = JsonNode.Parse(File.ReadAllText(Path.Combine(dirA, "session.json"), Encoding.UTF8)).AsObject();
            var sessB = JsonNode.Parse(File.ReadAllText(Path.Combine(dirB, "session.json"), Encoding.UTF8)).AsObject();
            bool outA = S(sessA["marks"]) == "output", outB = S(sessB["marks"]) == "output";
            var editsA = LoadEditsRaw(dirA);
            var editsB = LoadEditsRaw(dirB);

            string caption = S(sessA["caption"]);
            string name = "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_" + Recorder.SafeFolderPart((caption.Length > 0 ? caption : "記録") + "(合成)");
            string dir = Path.Combine(outRoot, name);
            for (int k = 2; Directory.Exists(dir); k++) dir = Path.Combine(outRoot, name + "-" + k);
            Directory.CreateDirectory(Path.Combine(dir, "img"));

            var lines = new List<string>();
            var edits = new JsonObject();
            int seq = 0;
            foreach (var r in plan.Where(p => p.Include))
            {
                bool fromA = r.Kind == RowKind.OnlyA || (r.Kind == RowKind.Both && r.UseA);
                string srcDir = fromA ? dirA : dirB;
                var src = fromA ? a[r.A] : b[r.B];
                var e = JsonNode.Parse(src.ToJsonString()).AsObject();
                long oldSeq = (long)Num(src["seq"]);
                seq++;
                e["seq"] = seq;
                e["tMs"] = seq * 1000L;   // まとめた順に並ぶように
                e["mergedFrom"] = fromA ? "A" : "B";
                // 印を描いて保存していた古い記録から来た手順には、出力するときに印を重ねない
                if ((outA || outB) && !(fromA ? outA : outB)) e["marksBaked"] = true;
                foreach (var tag in new[] { "before", "after" })
                {
                    var img = e[tag] as JsonObject;
                    string file = S(img?["file"]);
                    if (file.Length == 0) continue;
                    string newFile = string.Format(CultureInfo.InvariantCulture, "img/{0:D4}_{1}.png", seq, tag);
                    string from = Path.Combine(srcDir, file.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(from)) File.Copy(from, Path.Combine(dir, newFile.Replace('/', Path.DirectorySeparatorChar)), true);
                    img["file"] = newFile;
                }
                var srcEdits = fromA ? editsA : editsB;
                if (srcEdits != null && srcEdits[oldSeq.ToString(CultureInfo.InvariantCulture)] is JsonObject ed)
                    edits[seq.ToString(CultureInfo.InvariantCulture)] = JsonNode.Parse(ed.ToJsonString());
                lines.Add(e.ToJsonString(JsonOpts(false)));
            }
            File.WriteAllLines(Path.Combine(dir, "events.jsonl"), lines, new UTF8Encoding(false));
            if (edits.Count > 0)
                File.WriteAllText(Path.Combine(dir, "edits.json"), new JsonObject { ["steps"] = edits }.ToJsonString(JsonOpts(true)), new UTF8Encoding(false));

            var sess = JsonNode.Parse(sessA.ToJsonString()).AsObject();
            sess["eventCount"] = lines.Count;
            sess["mergedFrom"] = new JsonArray(Path.GetFileName(dirA.TrimEnd('\\', '/')), Path.GetFileName(dirB.TrimEnd('\\', '/')));
            if (outA || outB) sess["marks"] = "output"; else sess.Remove("marks");
            if (S(sessB["end"]).Length > 0) sess["end"] = S(sessB["end"]);
            File.WriteAllText(Path.Combine(dir, "session.json"), sess.ToJsonString(JsonOpts(true)), new UTF8Encoding(false));
            return dir;
        }

        static JsonObject LoadEditsRaw(string dir)
        {
            string p = Path.Combine(dir, "edits.json");
            try { return File.Exists(p) ? JsonNode.Parse(File.ReadAllText(p, Encoding.UTF8))?["steps"] as JsonObject : null; } catch { return null; }
        }

        static System.Text.Json.JsonSerializerOptions JsonOpts(bool indented) =>
            new System.Text.Json.JsonSerializerOptions { WriteIndented = indented, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    }
}
