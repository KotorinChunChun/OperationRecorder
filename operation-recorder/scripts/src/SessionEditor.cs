using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace OpRec
{
    // 記録フォルダを後から直す(アプリの「記録一覧」から使う)。画面に頼らないので、自動テストから直接確かめられる
    public static class SessionEditor
    {
        // 手順を 1 つ消す。events.jsonl のその行・その手順だけが使っている画像・edits.json の手直しを消し、
        // session.json の eventCount を直す。手順の番号は並び順で振るので、後ろの手順は 1 つずつ繰り上がる。
        // その seq の手順が無ければ false。deleteFile を渡すと、画像はそれで消す(アプリはごみ箱へ移す)
        public static bool DeleteStep(string sessionDir, long seq, Action<string> deleteFile = null)
        {
            string eventsPath = Path.Combine(sessionDir, "events.jsonl");
            if (!File.Exists(eventsPath)) return false;
            var lines = File.ReadAllLines(eventsPath, Encoding.UTF8).Where(l => l.Trim().Length > 0).ToList();
            int index = lines.FindIndex(l => SeqOf(l) == seq);
            if (index < 0) return false;

            var removed = JsonNode.Parse(lines[index]) as JsonObject;
            lines.RemoveAt(index);
            File.WriteAllLines(eventsPath, lines, new UTF8Encoding(false));

            // 画像は、ほかの手順が使っていなければ消す
            var stillUsed = new HashSet<string>(lines.SelectMany(FilesOf), StringComparer.OrdinalIgnoreCase);
            foreach (var f in FilesOf(removed))
            {
                if (stillUsed.Contains(f)) continue;
                string p = Path.Combine(sessionDir, f.Replace('/', Path.DirectorySeparatorChar));
                try { if (File.Exists(p)) { if (deleteFile != null) deleteFile(p); else File.Delete(p); } } catch { }
            }

            // 手直しした文章
            string editsPath = Path.Combine(sessionDir, "edits.json");
            if (File.Exists(editsPath))
            {
                var root = JsonNode.Parse(File.ReadAllText(editsPath, Encoding.UTF8)) as JsonObject;
                if (root?["steps"] is JsonObject steps && steps.Remove(seq.ToString(CultureInfo.InvariantCulture)))
                    File.WriteAllText(editsPath, root.ToJsonString(JsonOpts), new UTF8Encoding(false));
            }

            // 手順の数
            string sessionPath = Path.Combine(sessionDir, "session.json");
            if (File.Exists(sessionPath) && JsonNode.Parse(File.ReadAllText(sessionPath, Encoding.UTF8)) is JsonObject s)
            {
                s["eventCount"] = lines.Count;
                File.WriteAllText(sessionPath, s.ToJsonString(JsonOpts), new UTF8Encoding(false));
            }
            return true;
        }

        static readonly System.Text.Json.JsonSerializerOptions JsonOpts =
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        static long SeqOf(string line)
        {
            try { return (JsonNode.Parse(line)?["seq"] as JsonValue)?.GetValue<long>() ?? -1; } catch { return -1; }
        }

        static IEnumerable<string> FilesOf(string line)
        {
            JsonObject o;
            try { o = JsonNode.Parse(line) as JsonObject; } catch { yield break; }
            foreach (var f in FilesOf(o)) yield return f;
        }

        static IEnumerable<string> FilesOf(JsonObject o)
        {
            if (o == null) yield break;
            foreach (var k in new[] { "before", "after" })
            {
                string f = null;
                try { f = (o[k]?["file"] as JsonValue)?.GetValue<string>(); } catch { }
                if (!string.IsNullOrEmpty(f)) yield return f;
            }
        }
    }
}
