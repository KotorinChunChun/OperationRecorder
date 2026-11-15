using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpRec
{
    public static class SessionTranscript
    {
        public const string FileName = "transcript.json";
        public const long MaximumBytes = 8 * 1024 * 1024;

        public static int Save(string directory, JsonArray segments, long offsetMs)
        {
            if (offsetMs < 0 || offsetMs > TimeSpan.FromDays(1).TotalMilliseconds)
                throw new ArgumentException("音声開始時刻が範囲外です。");
            var normalized = new JsonArray();
            foreach (var item in segments)
            {
                if (!(item is JsonObject segment) ||
                    !(segment["startMs"] is JsonValue start) || !start.TryGetValue<long>(out var from) ||
                    !(segment["endMs"] is JsonValue end) || !end.TryGetValue<long>(out var to) ||
                    from < 0 || to <= from || to > TimeSpan.FromDays(1).TotalMilliseconds ||
                    !(segment["text"] is JsonValue text) || !text.TryGetValue<string>(out var caption) ||
                    caption == null || caption.Length > 8000)
                    throw new ArgumentException("文字起こし区間には有効な startMs、endMs、text が必要です。");
                if (!string.IsNullOrWhiteSpace(caption))
                    normalized.Add(new JsonObject { ["startMs"] = from + offsetMs, ["endMs"] = to + offsetMs, ["text"] = caption.Trim() });
            }
            string content = normalized.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            if (Encoding.UTF8.GetByteCount(content) > MaximumBytes) throw new ArgumentException("文字起こしが大きすぎます。");
            string target = Path.Combine(directory, FileName);
            string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, content, new UTF8Encoding(false));
                File.Move(temp, target, true);
            }
            finally { File.Delete(temp); }
            return normalized.Count;
        }

        internal static Dictionary<long, string> ByStep(string directory, List<JsonObject> events)
        {
            var result = new Dictionary<long, string>();
            string file = Path.Combine(directory, FileName);
            if (events.Count == 0 || !File.Exists(file) || new FileInfo(file).Length > MaximumBytes) return result;

            JsonArray segments;
            try { segments = JsonNode.Parse(File.ReadAllText(file)) as JsonArray; }
            catch (JsonException) { return result; }
            if (segments == null) return result;
            var timed = segments.OfType<JsonObject>()
                .Where(segment => segment["startMs"] is JsonValue clock && clock.TryGetValue<long>(out _) &&
                    segment["text"] is JsonValue text && text.TryGetValue<string>(out _))
                .OrderBy(segment => segment["startMs"].GetValue<long>());
            var bodies = new Dictionary<long, List<string>>();
            int index = 0;
            foreach (var segment in timed)
            {
                long at = segment["startMs"].GetValue<long>();
                if (at < 0 || at > TimeSpan.FromDays(2).TotalMilliseconds) continue;
                while (index + 1 < events.Count && events[index + 1]["tMs"] is JsonValue next &&
                       next.TryGetValue<double>(out var nextAt) && nextAt <= at) index++;
                if (!(events[index]["seq"] is JsonValue value) || !value.TryGetValue<long>(out var seq)) continue;
                if (!bodies.TryGetValue(seq, out var lines)) bodies[seq] = lines = new List<string>();
                var time = TimeSpan.FromMilliseconds(at);
                string text = EscapeMarkdown(segment["text"].GetValue<string>());
                lines.Add("[" + (int)time.TotalHours + time.ToString(@"\:mm\:ss\.fff", System.Globalization.CultureInfo.InvariantCulture) + "] " + text);
            }
            foreach (var body in bodies) result[body.Key] = string.Join("\n\n", body.Value);
            return result;
        }

        internal static string EscapeMarkdown(string value)
        {
            var text = new StringBuilder();
            foreach (char character in value ?? "")
            {
                if ("\\`*_{}[]<>()#+-.!|&~".IndexOf(character) >= 0) text.Append('\\');
                text.Append(char.IsControl(character) ? ' ' : character);
            }
            return text.ToString();
        }
    }
}
