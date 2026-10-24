using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpRec;

namespace OperationRecorderApp
{
    // GUIが使うサービスを、試験専用領域に限定して呼ぶアダプター。
    public static class BasicAutomation
    {
        static readonly Dictionary<string, Dictionary<string, string[]>> Fields = new Dictionary<string, Dictionary<string, string[]>>(StringComparer.Ordinal)
        {
            ["session"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["inspect"] = new[] { "session" }, ["metadata"] = new[] { "session", "caption", "description" },
                ["edit"] = new[] { "session", "seq", "headline", "body" },
                ["reset"] = new[] { "session", "seq" }, ["delete-step"] = new[] { "session", "seq" },
                ["export"] = new[] { "session", "templates", "out", "templateDirectory", "md", "html", "embed", "caption", "description", "marks", "radius" },
            },
            ["template"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["list"] = new[] { "directory" }, ["read"] = new[] { "directory", "name" },
                ["create"] = new[] { "directory", "name", "text" }, ["save"] = new[] { "directory", "name", "text" },
                ["delete"] = new[] { "directory", "name" }, ["check"] = new[] { "text" }, ["preview"] = new[] { "session", "text" },
            },
            ["settings"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["defaults"] = System.Array.Empty<string>(), ["load"] = System.Array.Empty<string>(), ["save"] = new[] { "settings" },
            },
        };

        public static JsonObject Execute(string area, JsonObject request, AutomationWorkspace workspace)
        {
            if (workspace == null) throw new ArgumentNullException(nameof(workspace));
            ValidateRequest(area, request);
            string action = Required(request, "action");
            switch (area)
            {
                case "session": return Session(action, request, workspace);
                case "template": return Template(action, request, workspace);
                case "settings": return Settings(action, request, workspace);
                default: throw new ArgumentException("未対応の自動操作領域です。");
            }
        }

        static JsonObject Session(string action, JsonObject request, AutomationWorkspace workspace)
        {
            var directory = SessionDirectory(request, workspace);
            var sessionPath = workspace.Resolve(Path.Combine(directory, "session.json"), true);
            var session = ReadObject(sessionPath);
            switch (action)
            {
                case "inspect": break;
                case "metadata":
                    if (request.ContainsKey("caption")) session["caption"] = Required(request, "caption").Trim();
                    if (request.ContainsKey("description")) session["description"] = Required(request, "description").Trim();
                    workspace.WriteJson(sessionPath, session);
                    break;
                case "edit":
                    ReportGenerator.SaveStepEdit(directory, Seq(request, directory), Required(request, "headline"), Required(request, "body"));
                    break;
                case "reset": ReportGenerator.SaveStepEdit(directory, Seq(request, directory), null, null); break;
                case "delete-step":
                    if (!SessionEditor.DeleteStep(directory, Seq(request, directory))) throw new IOException("削除する手順がありません。");
                    break;
                case "export":
                    var names = request["templates"] == null ? new[] { "user" } : Array(request, "templates").Select(node => StringValue(node, "templates")).ToArray();
                    if (names.Length == 0) throw new ArgumentException("テンプレートを1件以上指定してください。");
                    foreach (var name in names) CheckName(name);
                    var templateDir = Text(request, "templateDirectory");
                    string templatePath = templateDir == null ? null : workspace.Resolve(templateDir, true);
                    if (templatePath != null)
                    {
                        if (!Directory.Exists(templatePath)) throw new ArgumentException("templateDirectoryはフォルダーです。");
                        foreach (var name in names)
                        {
                            string effective = (name == "user-table" || name == "user-before") && !File.Exists(Path.Combine(templatePath, name + ".md")) ? "user" : name;
                            string file = effective.EndsWith(".md", StringComparison.Ordinal) ? effective : effective + ".md";
                            workspace.Resolve(Path.Combine(templatePath, file), true);
                        }
                    }
                    var output = workspace.EnsureDirectory(Text(request, "out") ?? "exports");
                    var options = new ExportCommand.Options
                    {
                        SessionDir = directory, OutDir = output, Templates = names.ToList(),
                        TemplateDir = templatePath,
                        Md = Boolean(request, "md", true), Html = Boolean(request, "html", true),
                        Embed = Boolean(request, "embed", false), Caption = Text(request, "caption"), Description = Text(request, "description"),
                        Marks = MarkOptions.Parse(Text(request, "marks") ?? "circle,arrow,frame", Integer(request, "radius", 14)),
                    };
                    if (!options.Md && !options.Html) throw new ArgumentException("出力形式を1件以上選択してください。");
                    var files = new JsonArray(); foreach (var file in ExportCommand.Run(options)) files.Add(file);
                    return new JsonObject { ["files"] = files, ["session"] = Inspect(directory) };
                default: throw new ArgumentException("未対応の記録操作です：" + action);
            }
            return Inspect(directory);
        }

        static JsonObject Template(string action, JsonObject request, AutomationWorkspace workspace)
        {
            if (action == "check") return new JsonObject { ["error"] = TemplateStore.Check(Required(request, "text")) };
            if (action == "preview") return new JsonObject { ["markdown"] = TemplateStore.Preview(SessionDirectory(request, workspace), Required(request, "text")) };
            var directory = workspace.EnsureDirectory(Text(request, "directory") ?? "templates");
            foreach (var file in Directory.EnumerateFiles(directory, "*.md")) workspace.Resolve(file, true);
            var name = Text(request, "name");
            if (action != "list" && action != "check" && action != "preview")
            {
                if (name == null) throw new ArgumentException("name が必要です。"); CheckName(name);
                workspace.Resolve(TemplateStore.PathOf(directory, name));
            }
            switch (action)
            {
                case "list":
                    var names = new JsonArray(); foreach (var item in TemplateStore.Names(directory)) names.Add(item);
                    return new JsonObject { ["names"] = names };
                case "read": return new JsonObject { ["name"] = name, ["text"] = TemplateStore.Read(directory, name) };
                case "create": TemplateStore.Create(directory, name, Text(request, "text")); break;
                case "save":
                    workspace.Resolve(TemplateStore.PathOf(directory, name));
                    if (!File.Exists(TemplateStore.PathOf(directory, name))) throw new FileNotFoundException("保存するテンプレートがありません。");
                    TemplateStore.Save(directory, name, Required(request, "text")); break;
                case "delete":
                    workspace.Resolve(TemplateStore.PathOf(directory, name)); TemplateStore.Delete(directory, name, false);
                    return new JsonObject { ["deleted"] = !File.Exists(TemplateStore.PathOf(directory, name)) };
                default: throw new ArgumentException("未対応のテンプレート操作です：" + action);
            }
            return new JsonObject { ["name"] = name, ["text"] = TemplateStore.Read(directory, name) };
        }

        static JsonObject Settings(string action, JsonObject request, AutomationWorkspace workspace)
        {
            string previous = AppSettings.SettingsDirectoryOverride;
            bool passive = AppSettings.Passive;
            try
            {
                AppSettings.SettingsDirectoryOverride = workspace.EnsureDirectory("settings"); AppSettings.Passive = true;
                workspace.Resolve("settings/settings.json");
                AppSettings settings;
                switch (action)
                {
                    case "load": settings = AppSettings.Load(); break;
                    case "defaults": settings = AppSettings.FromJson(null); break;
                    case "save":
                        if (!(request["settings"] is JsonObject values)) throw new ArgumentException("settings オブジェクトが必要です。");
                        settings = AppSettings.FromJson(values.ToJsonString()); break;
                    default: throw new ArgumentException("未対応の設定操作です：" + action);
                }
                settings.OutDir = workspace.EnsureDirectory("settings/records");
                settings.ReportDir = workspace.EnsureDirectory("settings/reports");
                if (!string.IsNullOrEmpty(settings.TemplateDir)) settings.TemplateDir = workspace.Resolve(settings.TemplateDir, true);
                settings.AskedShortcut = true;
                if (action == "save") settings.Save();
                return new JsonObject { ["settings"] = JsonNode.Parse(JsonSerializer.Serialize(settings)) };
            }
            finally { AppSettings.SettingsDirectoryOverride = previous; AppSettings.Passive = passive; }
        }

        public static JsonObject Inspect(string directory)
        {
            var session = ReadObject(Path.Combine(directory, "session.json"));
            var steps = new JsonArray();
            foreach (var step in ReportGenerator.ListSteps(directory)) steps.Add(new JsonObject
            {
                ["number"] = step.Number, ["seq"] = step.Seq, ["kind"] = step.Kind,
                ["headline"] = step.Headline, ["body"] = step.Body, ["edited"] = step.Edited,
                ["before"] = step.BeforePath, ["after"] = step.AfterPath,
            });
            return new JsonObject { ["directory"] = directory, ["caption"] = session["caption"]?.DeepClone(), ["description"] = session["description"]?.DeepClone(), ["steps"] = steps };
        }

        static long Seq(JsonObject request, string directory)
        {
            long seq = PositiveLong(request, "seq");
            if (!ReportGenerator.ListSteps(directory).Any(step => step.Seq == seq))
                throw new ArgumentException("存在する元手順番号seqを指定してください。");
            return seq;
        }

        internal static string SessionDirectory(JsonObject request, AutomationWorkspace workspace)
        {
            var directory = workspace.Resolve(Required(request, "session"), true);
            if (!Directory.Exists(directory)) throw new ArgumentException("session は記録フォルダーです。");
            ReadObject(workspace.Resolve(Path.Combine(directory, "session.json"), true));
            var eventsPath = workspace.Resolve(Path.Combine(directory, "events.jsonl"), true);
            if (new FileInfo(eventsPath).Length > 32 * 1024 * 1024) throw new ArgumentException("記録JSONが容量上限を超えています。");
            foreach (var line in File.ReadLines(eventsPath, Encoding.UTF8).Where(line => !string.IsNullOrWhiteSpace(line)))
            {
                var record = JsonNode.Parse(line) as JsonObject ?? throw new ArgumentException("記録イベントの形式が不正です。");
                foreach (var tag in new[] { "before", "after" })
                {
                    if (record[tag] == null) continue;
                    if (record[tag] is not JsonObject imageInfo || imageInfo["file"] is not JsonValue value || !value.TryGetValue<string>(out var relative))
                        throw new ArgumentException("画像の参照はfileを持つオブジェクトです。");
                    if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Replace('\\', '/').Split('/').Any(part => part == ".." || part.EndsWith(".") || part.EndsWith(" ")))
                        throw new ArgumentException("記録外の画像を参照できません。");
                    var image = workspace.Resolve(Path.Combine(directory, relative), true);
                    if (!AutomationWorkspace.Within(image, directory)) throw new ArgumentException("記録外の画像を参照できません。");
                }
            }
            var edits = Path.Combine(directory, "edits.json"); if (File.Exists(edits)) ReadObject(workspace.Resolve(edits, true));
            return directory;
        }

        static void ValidateRequest(string area, JsonObject request)
        {
            if (request == null || request["schemaVersion"] is not JsonValue schema || !schema.TryGetValue<int>(out var version) || version != 1)
                throw new ArgumentException("schemaVersion=1が必要です。");
            string action = Required(request, "action");
            if (area == null || !Fields.TryGetValue(area, out var actions)) throw new ArgumentException("未対応の自動操作領域です。");
            if (!actions.TryGetValue(action, out var fields)) throw new ArgumentException("未対応の操作です：" + action);
            var allowed = new HashSet<string>(fields.Concat(new[] { "schemaVersion", "action" }), StringComparer.Ordinal);
            foreach (var field in request)
            {
                if (!allowed.Contains(field.Key)) throw new ArgumentException("知らない入力項目です：" + field.Key);
                switch (field.Key)
                {
                    case "schemaVersion": case "action": break;
                    case "settings": ValidateSettings(field.Value); break;
                    case "seq": PositiveLong(request, field.Key); break;
                    case "radius": Integer(request, field.Key, 14); if (field.Value == null) throw new ArgumentException("radiusは0～200の整数です。"); break;
                    case "md": case "html": case "embed":
                        if (field.Value == null) throw new ArgumentException(field.Key + "はtrue/falseです。");
                        Boolean(request, field.Key, true); break;
                    case "templates":
                        var templates = Array(request, field.Key);
                        if (templates.Count == 0) throw new ArgumentException("templatesは1件以上の配列です。");
                        foreach (var item in templates) CheckName(StringValue(item, field.Key));
                        break;
                    default:
                        string text = StringValue(field.Value, field.Key);
                        if (new[] { "session", "directory", "out", "templateDirectory" }.Contains(field.Key) && string.IsNullOrWhiteSpace(text))
                            throw new ArgumentException(field.Key + "は空でないパスです。");
                        if (field.Key == "name") CheckName(text);
                        break;
                }
            }
            if (area == "session" || area == "template" && action == "preview") Required(request, "session");
            if (area == "session")
            {
                if (action == "metadata" && !request.ContainsKey("caption") && !request.ContainsKey("description"))
                    throw new ArgumentException("metadataにはcaptionかdescriptionが必要です。");
                if (action == "edit") { Required(request, "headline"); Required(request, "body"); }
                if (action == "edit" || action == "reset" || action == "delete-step") PositiveLong(request, "seq");
                if (action == "export")
                {
                    if (!Boolean(request, "md", true) && !Boolean(request, "html", true)) throw new ArgumentException("出力形式を1件以上選択してください。");
                    MarkOptions.Parse(Text(request, "marks") ?? "circle,arrow,frame", Integer(request, "radius", 14));
                }
            }
            if (area == "template")
            {
                if (action == "read" || action == "create" || action == "save" || action == "delete") CheckName(Required(request, "name"));
                if (action == "save" || action == "check" || action == "preview") Required(request, "text");
            }
            if (area == "settings" && action == "save" && !request.ContainsKey("settings")) throw new ArgumentException("settingsオブジェクトが必要です。");
        }
        static void ValidateSettings(JsonNode node)
        {
            if (node is not JsonObject settings) throw new ArgumentException("settingsオブジェクトが必要です。");
            try { JsonSerializer.Deserialize<AppSettings>(settings.ToJsonString(), new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }); }
            catch (JsonException error) { throw new ArgumentException("settingsに不明な項目または型が異なる値があります。", error); }
        }
        static long PositiveLong(JsonObject request, string name)
        {
            if (request[name] is JsonValue value)
            {
                if (value.TryGetValue<long>(out var number) && number > 0) return number;
                if (value.TryGetValue<int>(out var integer) && integer > 0) return integer;
            }
            throw new ArgumentException(name + "は正の整数です。");
        }

        internal static JsonObject ReadObject(string path)
        {
            if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new ArgumentException("JSONが容量上限を超えています。");
            return JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject ?? throw new ArgumentException("JSONオブジェクトが必要です。");
        }
        internal static string Required(JsonObject node, string key) => Text(node, key) ?? throw new ArgumentException(key + " が必要です。");
        internal static string Text(JsonObject node, string key) => node[key] == null ? null : StringValue(node[key], key);
        static string StringValue(JsonNode node, string key) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw new ArgumentException(key + " は文字列です。");
        internal static JsonArray Array(JsonObject node, string key) => node[key] as JsonArray ?? throw new ArgumentException(key + " は配列です。");
        static bool Boolean(JsonObject node, string key, bool fallback) => node[key] == null ? fallback : node[key] is JsonValue value && value.TryGetValue<bool>(out var result) ? result : throw new ArgumentException(key + " は真偽値です。");
        static int Integer(JsonObject node, string key, int fallback)
        {
            if (node[key] == null) return fallback;
            if (node[key] is JsonValue value && value.TryGetValue<int>(out var result) && result >= 0 && result <= 200) return result;
            throw new ArgumentException(key + " は0～200の整数です。");
        }
        static void CheckName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.Length > 60 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(',') || name.EndsWith(".") || name != Path.GetFileName(name))
                throw new ArgumentException("テンプレート名が不正です。");
        }
    }
}
