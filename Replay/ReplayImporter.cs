using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace OperationRecorderApp.Replay;

public static class ReplayImporter
{
    public static string SourceHash(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in new[] { "session.json", "events.jsonl" }) hash.AppendData(File.ReadAllBytes(Path.Combine(directory, name)));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    public static string StableClass(string value) => (value ?? "").Split(new[] { ".app." }, StringSplitOptions.None)[0];
    public static ReplayConfig Import(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (new FileInfo(Path.Combine(directory, "events.jsonl")).Length > 32 * 1024 * 1024) throw new ArgumentException("記録が大きすぎます。");
        var config = new ReplayConfig { SourceDirectory = directory, SourceSha256 = SourceHash(directory) };
        var lines = File.ReadLines(Path.Combine(directory, "events.jsonl")).Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => JsonNode.Parse(s)?.AsObject() ?? throw new ArgumentException("イベント形式が不正です。"))
            .OrderBy(s => Number(s["tMs"])).ThenBy(s => Number(s["seq"])).ToArray();
        var bindings = new Dictionary<string, string>(); long previous = 0;
        foreach (var e in lines)
        {
            var w = e["window"] as JsonObject;
            string targetKey = Text(w?["processName"]) + "|" + Text(w?["className"]) + "|" + Text(w?["title"]);
            if (!bindings.TryGetValue(targetKey, out var targetId))
            {
                targetId = "window" + (bindings.Count + 1); bindings.Add(targetKey, targetId);
                config.Targets[targetId] = new ReplayTarget
                {
                    Executable = Text(w?["program"]?["path"]) ?? "", Title = Text(w?["title"]) ?? "",
                    ClassPrefix = StableClass(Text(w?["className"])), RecordedWidth = (int)Number(w?["frame"]?["w"]),
                    RecordedHeight = (int)Number(w?["frame"]?["h"]), RecordedDpi = (int)Number(w?["dpi"]),
                };
            }
            string kind = Text(e["kind"]) ?? "unsupported";
            long time = Number(e["tMs"]);
            var step = new ReplayStep { SourceSeq = Number(e["seq"]), Kind = kind, Target = targetId, DelayMs = (int)Math.Clamp(time - previous, 0, 600000), Key = Text(e["key"]) };
            previous = time;
            if (e["uia"] is JsonObject uia)
            {
                string id = Text(uia["automationId"]) ?? "";
                step.Element = new ReplayElement { AutomationId = id.All(char.IsDigit) ? "" : id, Name = Text(uia["name"]) ?? "", ControlType = Text(uia["controlType"]) ?? "", ClassPrefix = StableClass(Text(uia["className"])) };
                var r = uia["boundsInWindow"];
                double width = Number(r?["w"]), height = Number(r?["h"]);
                if (width > 0 && height > 0 && e["pointInWindow"] != null)
                {
                    step.X = Math.Clamp((Number(e["pointInWindow"]?["x"]) - Number(r?["x"])) / width, 0, 1);
                    step.Y = Math.Clamp((Number(e["pointInWindow"]?["y"]) - Number(r?["y"])) / height, 0, 1);
                    if (e["dragEndInWindow"] != null)
                    {
                        step.EndX = (Number(e["dragEndInWindow"]?["x"]) - Number(r?["x"])) / width;
                        step.EndY = (Number(e["dragEndInWindow"]?["y"]) - Number(r?["y"])) / height;
                    }
                }
            }
            if (kind == "input")
            {
                step.IsPassword = e["input"]?["isPassword"]?.GetValue<bool>() ?? false;
                step.Text = new ReplayText { Literal = step.IsPassword ? null : Text(e["input"]?["value"]) };
            }
            if (kind == "wheel") step.WheelNotches = checked((int)Number(e["wheel"]?["notches"])) * (Text(e["wheel"]?["direction"]) == "up" ? 1 : -1);
            if (kind == "capture" || kind == "note") step.Kind = "wait";
            step.BeforeImage = Gate(e, "before", directory);
            step.AfterImage = Gate(e, "after", directory);
            config.Steps.Add(step);
        }
        return config;
    }
    static ReplayImage Gate(JsonObject e, string tag, string directory)
    {
        var img = e[tag]; var frame = e["window"]?["frame"]; var area = img?["area"];
        if (img == null || area == null || frame == null) return null;
        int x = (int)(Number(frame["x"]) - Number(area["x"])), y = (int)(Number(frame["y"]) - Number(area["y"]));
        int width = (int)Number(frame["w"]), height = (int)Number(frame["h"]);
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > Number(area["w"]) || y + height > Number(area["h"])) return null;
        string relative = Text(img["file"]);
        if (string.IsNullOrWhiteSpace(relative)) return null;
        string file = Path.GetFullPath(relative, directory);
        if (!file.StartsWith(directory.TrimEnd('\\') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("元記録の外の画像は参照できません。");
        return new ReplayImage { File = file, Crop = new ReplayRect { X = x, Y = y, W = width, H = height } };
    }
    static string Text(JsonNode node) => node == null ? null : node.GetValue<string>();
    static long Number(JsonNode node) => node == null ? 0 : node.GetValue<long>();
}
