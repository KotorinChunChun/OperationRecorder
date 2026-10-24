using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace OperationRecorderApp
{
    // 自動操作の書込み先は、明示して作った専用領域だけにする。
    public sealed class AutomationWorkspace
    {
        public const string MarkerName = ".operation-recorder-automation.json";
        public string Root { get; }

        public AutomationWorkspace(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("--workspace を指定してください。");
            Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Root.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("自動試験にはローカルドライブの専用領域を指定してください。");
            if (Root.Length == 0 || string.Equals(Root, Path.GetPathRoot(Root)?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("ドライブのルートを試験領域にできません。");
            CheckLinks(Root);
            var normalSettings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OperationRecorder");
            if (Within(Root, normalSettings) || Within(Root, AppSettings.DefaultOutDir))
                throw new ArgumentException("通常の設定・記録先を自動操作の領域にできません。");
            var marker = Path.Combine(Root, MarkerName);
            if (Directory.Exists(Root) && !File.Exists(marker) && Directory.EnumerateFileSystemEntries(Root).Any())
                throw new ArgumentException("空のフォルダーか、以前作成した自動操作の専用領域を指定してください。");
            Directory.CreateDirectory(Root);
            if (File.Exists(marker))
            {
                CheckLinks(marker);
                if (new FileInfo(marker).Length > 4096) throw new ArgumentException("試験領域の所有情報が不正です。");
                JsonNode data;
                try { data = JsonNode.Parse(File.ReadAllText(marker, Encoding.UTF8)); }
                catch (System.Text.Json.JsonException ex) { throw new ArgumentException("試験領域の所有情報が壊れています。", ex); }
                if (data?["schemaVersion"]?.ToString() != "1" || data?["product"]?.ToString() != "OperationRecorder" ||
                    !Guid.TryParseExact(data?["id"]?.ToString(), "N", out _))
                    throw new ArgumentException("試験領域の所有情報が不正です。");
            }
            else
            {
                var data = new JsonObject { ["schemaVersion"] = 1, ["product"] = "OperationRecorder", ["id"] = Guid.NewGuid().ToString("N") };
                using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(data.ToJsonString());
            }
        }

        public string Resolve(string path, bool mustExist = false)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("試験領域内のパスを指定してください。");
            var full = Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(Root, path));
            if (!Within(full, Root) || string.Equals(full, Path.Combine(Root, MarkerName), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("試験領域の外や所有情報への参照は使用できません。");
            if (Path.GetRelativePath(Root, full).Replace('\\', '/').Split('/').Any(part => part != "." && (part.Contains(':') || part.EndsWith(".") || part.EndsWith(" "))))
                throw new ArgumentException("正規化できない試験用パスです。");
            CheckLinks(full);
            if (mustExist && !File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException("試験対象がありません。", full);
            return full;
        }

        public string EnsureDirectory(string relative)
        {
            var full = Resolve(relative);
            Directory.CreateDirectory(full);
            return full;
        }

        public void WriteJson(string path, JsonNode value)
        {
            var full = Resolve(path);
            if (Directory.Exists(full)) throw new ArgumentException("結果JSONの保存先がフォルダーです。");
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(value.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                }
                if (File.Exists(full)) File.Replace(temporary, full, null); else File.Move(temporary, full);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static bool Within(string path, string root)
        {
            path = Path.GetFullPath(path).TrimEnd('\\', '/'); root = Path.GetFullPath(root).TrimEnd('\\', '/');
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        internal static void CheckLinks(string path)
        {
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("リンクを含む試験領域・ファイルは使用できません。");
                current = Path.GetDirectoryName(current);
            }
        }
    }
}
