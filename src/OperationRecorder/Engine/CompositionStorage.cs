using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpRec
{
    public sealed partial class CompositionProject
    {
        sealed class InventoryFile
        {
            public string Path { get; set; }
            public long Length { get; set; }
            public string Sha256 { get; set; }
        }
        sealed class Inventory
        {
            public int SchemaVersion { get; set; } = 1;
            public List<InventoryFile> Files { get; set; } = new List<InventoryFile>();
        }

        static string Hash(string path)
        {
            using (var stream = File.OpenRead(path)) return Convert.ToHexString(SHA256.HashData(stream));
        }

        static bool IsWithin(string path, string root)
        {
            path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        static void CheckNoLinks(string path)
        {
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("リンクを含む保存先・参照画像は使用できません。");
                var parent = Path.GetDirectoryName(current);
                if (parent == current) break;
                current = parent;
            }
        }

        static string SafeFile(string directory, string relative)
        {
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\0'))
                throw new IOException("記録内のファイル参照が不正です。");
            var parts = relative.Replace('\\', '/').Split('/');
            if (parts.Any(part => string.IsNullOrEmpty(part) || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal)))
                throw new IOException("記録の外を指すファイル参照は使用できません。");
            var file = Path.GetFullPath(Path.Combine(directory, Path.Combine(parts)));
            if (!IsWithin(file, directory) || string.Equals(file, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
                throw new IOException("記録の外を指すファイル参照は使用できません。");
            CheckNoLinks(file);
            return file;
        }

        static string ReadJsonText(string path)
        {
            CheckNoLinks(path);
            if (!File.Exists(path)) throw new IOException("必要な記録ファイルがありません：" + Path.GetFileName(path));
            if (new FileInfo(path).Length > MaximumJsonBytes) throw new IOException("記録JSONが容量上限を超えています。");
            return File.ReadAllText(path, Encoding.UTF8);
        }

        static JsonObject ReadObject(string path)
        {
            try { return JsonNode.Parse(ReadJsonText(path)) as JsonObject ?? throw new IOException("記録JSONはオブジェクトである必要があります。"); }
            catch (JsonException error) { throw new IOException("記録JSONが壊れています：" + Path.GetFileName(path), error); }
        }

        static List<JsonObject> ReadEvents(string directory)
        {
            var result = new List<JsonObject>();
            var unique = new HashSet<long>();
            foreach (var line in ReadJsonText(SafeFile(directory, "events.jsonl")).Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonObject value;
                try { value = JsonNode.Parse(line) as JsonObject ?? throw new IOException("操作記録の形式が不正です。"); }
                catch (JsonException error) { throw new IOException("操作記録のJSONが壊れています。", error); }
                if (!unique.Add(Seq(value))) throw new IOException("記録の手順番号が重複しています。");
                if (value["tMs"] != null && (!(value["tMs"] is JsonValue time) || !time.TryGetValue<double>(out var ms) || !double.IsFinite(ms)))
                    throw new IOException("記録の時刻が不正です。");
                if (string.IsNullOrEmpty(Text(value["kind"]))) throw new IOException("操作の種類がありません。");
                result.Add(value);
                if (result.Count > MaximumSteps) throw new IOException("元記録の手順は10,000件までです。");
            }
            if (result.Count == 0) throw new IOException("操作手順がない記録は合成できません。");
            return result.OrderBy(value => value["tMs"] == null ? 0 : value["tMs"].GetValue<double>()).ThenBy(Seq).ToList();
        }

        static IEnumerable<string> ImageReferences(IEnumerable<JsonObject> events)
        {
            foreach (var value in events)
                foreach (var tag in new[] { "before", "after" })
                {
                    if (value[tag] == null) continue;
                    if (!(value[tag] is JsonObject image) || !(image["file"] is JsonValue file) || !file.TryGetValue<string>(out var relative) || string.IsNullOrEmpty(relative))
                        throw new IOException("操作画像の参照が不正です。");
                    yield return relative;
                }
        }

        static void CheckImage(string path)
        {
            CheckNoLinks(path);
            if (!File.Exists(path)) throw new IOException("記録の参照画像がありません：" + Path.GetFileName(path));
            var length = new FileInfo(path).Length;
            if (length == 0 || length > MaximumImageBytes) throw new IOException("参照画像の容量が不正です。");
            try
            {
                using (var image = Image.FromFile(path))
                    if (image.Width < 1 || image.Height < 1 || (long)image.Width * image.Height > 100000000)
                        throw new IOException("参照画像の寸法が上限を超えています。");
            }
            catch (ArgumentException error) { throw new IOException("参照画像を読み込めません：" + Path.GetFileName(path), error); }
            catch (OutOfMemoryException error) { throw new IOException("参照画像が壊れているか、読み込み上限を超えています。", error); }
            catch (System.Runtime.InteropServices.ExternalException error) { throw new IOException("参照画像を読み込めません：" + Path.GetFileName(path), error); }
        }

        static void ImportSnapshot(string original, string target)
        {
            CheckNoLinks(original);
            var session = ReadObject(SafeFile(original, "session.json"));
            var events = ReadEvents(original);
            var files = new List<string> { "session.json", "events.jsonl" };
            var editsPath = SafeFile(original, "edits.json");
            if (File.Exists(editsPath))
            {
                var edits = ReadObject(editsPath);
                if (edits["steps"] != null && !(edits["steps"] is JsonObject)) throw new IOException("手順の編集情報が不正です。");
                files.Add("edits.json");
            }
            var images = ImageReferences(events).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var image in images) CheckImage(SafeFile(original, image));
            files.AddRange(images);
            if (files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count) throw new IOException("画像参照が記録JSONと重複しています。");
            Directory.CreateDirectory(target);
            var inventory = new Inventory();
            long total = 0;
            foreach (var relative in files)
            {
                var from = SafeFile(original, relative);
                var to = SafeFile(target, relative);
                var size = new FileInfo(from).Length;
                total += size;
                if (total > 8L * 1024 * 1024 * 1024) throw new IOException("1記録の画像合計が8GBを超えています。");
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                var expected = Hash(from);
                File.Copy(from, to, false);
                if (new FileInfo(to).Length != size || Hash(to) != expected) throw new IOException("コピー中に元記録が変更されました。");
                inventory.Files.Add(new InventoryFile { Path = relative.Replace('\\', '/'), Length = size, Sha256 = expected });
            }
            File.WriteAllText(Path.Combine(target, "snapshot.files.json"), JsonSerializer.Serialize(inventory, JsonOptions), new UTF8Encoding(false));
        }

        static LoadedSource ReadSnapshot(string directory)
        {
            CheckNoLinks(directory);
            Inventory inventory;
            try { inventory = JsonSerializer.Deserialize<Inventory>(ReadJsonText(SafeFile(directory, "snapshot.files.json")), JsonOptions); }
            catch (JsonException error) { throw new IOException("合成元のファイル台帳が壊れています。", error); }
            if (inventory == null || inventory.SchemaVersion != 1 || inventory.Files == null || inventory.Files.Count < 2 || inventory.Files.Count > MaximumSteps * 2 + 3)
                throw new IOException("合成元のファイル台帳が不正です。");
            var listed = new Dictionary<string, InventoryFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in inventory.Files)
            {
                if (entry == null || entry.Path == null || !listed.TryAdd(entry.Path.Replace('\\', '/'), entry) || entry.Length < 0 || entry.Sha256 == null || entry.Sha256.Length != 64 || entry.Sha256.Any(c => !Uri.IsHexDigit(c)))
                    throw new IOException("合成元のファイル台帳が不正です。");
                var path = SafeFile(directory, entry.Path);
                if (!File.Exists(path) || new FileInfo(path).Length != entry.Length || !string.Equals(Hash(path), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("保存済みの合成元ファイルが欠落または変更されています：" + entry.Path);
            }
            if (!listed.ContainsKey("session.json") || !listed.ContainsKey("events.jsonl")) throw new IOException("合成元のファイル台帳に記録JSONがありません。");
            var session = ReadObject(SafeFile(directory, "session.json"));
            var events = ReadEvents(directory);
            foreach (var relative in ImageReferences(events))
            {
                if (!listed.ContainsKey(relative.Replace('\\', '/'))) throw new IOException("合成元の台帳に参照画像がありません。");
                CheckImage(SafeFile(directory, relative));
            }
            var editPath = SafeFile(directory, "edits.json");
            if (File.Exists(editPath) && !listed.ContainsKey("edits.json")) throw new IOException("台帳にない手順編集情報があります。");
            var steps = ReportGenerator.ListSteps(directory);
            return new LoadedSource { Session = session, Events = events, BySeq = events.ToDictionary(Seq), Steps = steps, Files = listed };
        }

        static void AtomicWrite(string path, string text)
        {
            CheckNoLinks(path);
            var temporary = Path.Combine(Path.GetDirectoryName(path), ".draft-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(text);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        static void DeleteOwnedDirectory(string directory, string parent)
        {
            if (!Directory.Exists(directory)) return;
            if (!IsWithin(directory, parent) || string.Equals(Path.GetFullPath(directory), Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase))
                throw new IOException("合成の一時フォルダー以外は削除しません。");
            CheckNoLinks(directory);
            // 再解析点をたどる再帰削除はしない。
            CheckTree(directory);
            Directory.Delete(directory, true);
        }

        static void CheckTree(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                CheckNoLinks(path);
                if (Directory.Exists(path)) CheckTree(path);
            }
        }

        public string Complete(string outputRoot)
        {
            loaded = Validate(document);
            if (!Rows.Any(row => row.Included)) throw new IOException("完成記録に含める手順を1件以上選んでください。");
            var root = Path.GetFullPath(outputRoot);
            if (IsWithin(root, DirectoryPath) || Sources.Any(source => IsWithin(root, source.OriginalDirectory)))
                throw new IOException("元記録や合成作業フォルダーの中へ完成記録を保存できません。");
            Directory.CreateDirectory(root);
            CheckNoLinks(root);
            var staging = Path.Combine(root, ".composition-output-" + Guid.NewGuid().ToString("N"));
            var name = "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + Recorder.SafeFolderPart(string.IsNullOrWhiteSpace(Caption) ? "合成した記録" : Caption);
            var destination = Path.Combine(root, name);
            for (var suffix = 2; Directory.Exists(destination) || File.Exists(destination); suffix++) destination = Path.Combine(root, name + "-" + suffix);
            try
            {
                Directory.CreateDirectory(Path.Combine(staging, "img"));
                var lines = new List<string>();
                var edits = new JsonObject();
                bool outputMarks = Rows.Where(row => row.Included && row.Candidates.Count > 0).Any(row => Text(Source(Selected(row).SourceId).Session["marks"]) == "output");
                var seq = 0;
                foreach (var row in Rows.Where(row => row.Included))
                {
                    seq++;
                    var reference = row.Candidates.Count > 0 ? Selected(row) : null;
                    var source = reference == null ? null : Source(reference.SourceId);
                    var value = source == null ? new JsonObject { ["kind"] = "note" } : source.BySeq[reference.Seq].DeepClone().AsObject();
                    value["seq"] = seq;
                    value["tMs"] = seq * 1000L;
                    value["mergedFrom"] = reference?.SourceId ?? "manual";
                    value["compositionOrigin"] = new JsonObject { ["rowId"] = row.Id, ["sourceId"] = reference?.SourceId, ["seq"] = reference?.Seq };
                    if (outputMarks && source != null && Text(source.Session["marks"]) != "output") value["marksBaked"] = true;
                    foreach (var tag in new[] { "before", "after" })
                    {
                        if (!(value[tag] is JsonObject image)) continue;
                        var original = Text(image["file"]);
                        var sourcePath = SafeFile(Path.Combine(DirectoryPath, "sources", reference.SourceId), original);
                        var relative = "img/" + seq.ToString("D4", CultureInfo.InvariantCulture) + "_" + tag + ".png";
                        var target = SafeFile(staging, relative);
                        File.Copy(sourcePath, target, false);
                        var expected = source.Files[original.Replace('\\', '/')];
                        if (new FileInfo(target).Length != expected.Length || !string.Equals(Hash(target), expected.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("完成保存中に参照画像が変更されました。");
                        image["file"] = relative;
                    }
                    var step = GetStep(row);
                    edits[seq.ToString(CultureInfo.InvariantCulture)] = new JsonObject { ["headline"] = step.Headline ?? "", ["body"] = step.Body ?? "" };
                    lines.Add(value.ToJsonString());
                }
                var session = Source(Sources[0].Id).Session.DeepClone().AsObject();
                session["caption"] = Caption;
                session["description"] = Description;
                session["eventCount"] = lines.Count;
                session["composedAt"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
                session["compositionSchemaVersion"] = CurrentSchemaVersion;
                // 合成日時とは別に、元記録全体を含む期間を保持する。
                var starts = Sources.Select(source => Text(Source(source.Id).Session["start"]))
                    .Where(value => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out _))
                    .OrderBy(value => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal)).ToArray();
                var ends = Sources.Select(source => Text(Source(source.Id).Session["end"]))
                    .Where(value => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out _))
                    .OrderBy(value => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal)).ToArray();
                if (starts.Length > 0) session["start"] = starts[0]; else session.Remove("start");
                if (ends.Length > 0) session["end"] = ends[ends.Length - 1]; else session.Remove("end");
                session["mergedFrom"] = new JsonArray(Sources.Select(source => (JsonNode)new JsonObject
                {
                    ["id"] = source.Id, ["caption"] = source.Caption, ["record"] = Path.GetFileName(source.OriginalDirectory.TrimEnd('\\', '/')),
                }).ToArray());
                if (outputMarks) session["marks"] = "output"; else session.Remove("marks");
                File.WriteAllLines(Path.Combine(staging, "events.jsonl"), lines, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(staging, "edits.json"), new JsonObject { ["steps"] = edits }.ToJsonString(JsonOptions), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(staging, "session.json"), session.ToJsonString(JsonOptions), new UTF8Encoding(false));
                if (ReportGenerator.ListSteps(staging).Count != lines.Count) throw new IOException("完成記録の検証に失敗しました。");
                Directory.Move(staging, destination);
                return destination;
            }
            catch
            {
                DeleteOwnedDirectory(staging, root);
                throw;
            }
        }
    }
}
