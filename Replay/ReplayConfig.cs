using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpRec;

namespace OperationRecorderApp.Replay;

public sealed class ReplayConfig
{
    public int SchemaVersion { get; set; } = 1;
    public string SourceDirectory { get; set; }
    public string SourceSha256 { get; set; }
    public Dictionary<string, ReplayTarget> Targets { get; set; } = new();
    public List<ReplayStep> Steps { get; set; } = new();
    public List<ReplayTrigger> Triggers { get; set; } = new() { new() };
    public int PollMs { get; set; } = 100;
    public int TimeoutMs { get; set; } = 30000;
    public int StableMs { get; set; } = 200;
    public int StartDelayMs { get; set; } = 1000;
    public int MaxRecordedGapMs { get; set; } = 2000;
    public double Speed { get; set; } = 1;
    public int MaxRuns { get; set; } = 1;
    public string StopKey { get; set; } = "Ctrl+Alt+Pause";
    [JsonIgnore] public string BaseDirectory { get; set; } = Environment.CurrentDirectory;
    public static JsonSerializerOptions JsonOptions => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    public static ReplayConfig Load(string file)
    {
        file = Path.GetFullPath(file);
        if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new ArgumentException("configが大きすぎます。");
        var value = JsonSerializer.Deserialize<ReplayConfig>(File.ReadAllText(file), JsonOptions) ?? throw new ArgumentException("configが空です。");
        value.BaseDirectory = Path.GetDirectoryName(file); return value;
    }
    public string PathOf(string path) => Path.GetFullPath(path, BaseDirectory);
    public void Save(string file)
    {
        file = Path.GetFullPath(file);
        if (new[] { "session.json", "events.jsonl", "edits.json" }.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("記録ファイルを上書きできません。");
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions)); File.Move(temporary, file, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public List<string> Validate(bool checkFiles = true)
    {
        var errors = new List<string>();
        if (SchemaVersion != 1) errors.Add("schemaVersionは1です。");
        if (Targets == null || Steps == null || Triggers == null) { errors.Add("targets/steps/triggersが必要です。"); return errors; }
        if (Steps.Count == 0 || Steps.Count > 10000) errors.Add("手順は1～10000件です。");
        if (PollMs < 25 || PollMs > 5000 || TimeoutMs < 0 || TimeoutMs > 1800000 || StableMs < 0 || StableMs > 30000 || StartDelayMs < 0 || StartDelayMs > 600000 || MaxRecordedGapMs < 0 || MaxRecordedGapMs > 600000 || !double.IsFinite(Speed) || Speed <= 0 || Speed > 100 || MaxRuns < 0) errors.Add("待機・速度・回数の設定が範囲外です。");
        try { ReplayKeys.Parse(StopKey, true); } catch (ArgumentException) { errors.Add("非常停止キーが不正です。"); }
        foreach (var target in Targets)
        {
            var t = target.Value;
            if (t == null || string.IsNullOrWhiteSpace(t.Executable) || !Path.IsPathFullyQualified(t.Executable)) errors.Add("対象 " + target.Key + " のexecutableに絶対パスが必要です。");
            else if (checkFiles && !File.Exists(t.Executable)) errors.Add("対象 " + target.Key + " のEXEがありません。");
            if (t != null && (t.ProcessId < 0 || t.RecordedWidth < 0 || t.RecordedHeight < 0 || t.RecordedDpi < 0 || !double.IsFinite(t.MaxAspectChange) || t.MaxAspectChange < 0 || t.MaxAspectChange > 1)) errors.Add("対象 " + target.Key + " の幾何条件が不正です。");
        }
        for (int index = 0; index < Steps.Count; index++)
        {
            var s = Steps[index]; string label = "手順 " + (index + 1) + ": ";
            if (s == null) { errors.Add(label + "空です。"); continue; }
            if (s.Skip) { if (string.IsNullOrWhiteSpace(s.SkipReason)) errors.Add(label + "skipReasonが必要です。"); continue; }
            if (!new[] { "click", "rightclick", "middleclick", "doubleclick", "drag", "rightdrag", "wheel", "key", "input", "wait", "launch" }.Contains(s.Kind)) errors.Add(label + "未対応の操作です。補正または明示skipが必要です。");
            if (s.Target == null || !Targets.ContainsKey(s.Target)) errors.Add(label + "対象bindingがありません。");
            if (s.DelayMs < 0 || s.DelayMs > 600000 || s.TimeoutMs < 0 || s.TimeoutMs > 1800000) errors.Add(label + "待機時間が範囲外です。");
            if (s.PositionMode != "element" && s.PositionMode != "window") errors.Add(label + "positionModeはelement/windowです。");
            if (s.Kind != "wait" && s.Kind != "launch")
            {
                if (s.PositionMode == "element" && (s.Element == null || !s.Element.IsSpecific)) errors.Add(label + "UIA識別子、または名前＋ControlTypeが必要です。");
                if (!ReplayGeometry.IsUnitPoint(s.X, s.Y) || !ReplayGeometry.IsUnitPoint(s.EndX, s.EndY)) errors.Add(label + "相対位置は0～1です。");
                if (s.PositionMode == "window" && (s.Target == null || !Targets.TryGetValue(s.Target, out var t) || t.RecordedWidth <= 0 || t.RecordedHeight <= 0)) errors.Add(label + "相対座標には記録時のウィンドウ寸法が必要です。");
            }
            if (s.Kind == "input")
            {
                if (s.Text == null || (s.Text.Literal == null && string.IsNullOrWhiteSpace(s.Text.Environment)) || (s.Text.Literal != null && !string.IsNullOrWhiteSpace(s.Text.Environment))) errors.Add(label + "入力値か環境変数を一方だけ補完してください。");
                if (s.IsPassword && (s.Text == null || string.IsNullOrWhiteSpace(s.Text.Environment))) errors.Add(label + "passwordは環境変数参照が必要です。");
            }
            if (s.Kind == "key") { try { ReplayKeys.Parse(s.Key); } catch (ArgumentException) { errors.Add(label + "再現できないキーです。"); } }
            if (s.Kind == "wheel" && (s.WheelNotches == 0 || Math.Abs((long)s.WheelNotches) > 100)) errors.Add(label + "ホイールは±1～100目盛りです。");
            foreach (var gate in new[] { s.BeforeImage, s.AfterImage }.Where(x => x != null))
            {
                if (string.IsNullOrWhiteSpace(gate.File) || !double.IsFinite(gate.Threshold) || gate.Threshold < 0 || gate.Threshold > 1 || (gate.Crop != null && (gate.Crop.X < 0 || gate.Crop.Y < 0 || gate.Crop.W <= 0 || gate.Crop.H <= 0))) errors.Add(label + "画像条件が不正です。");
                else if (checkFiles && !File.Exists(PathOf(gate.File))) errors.Add(label + "照合画像がありません。");
            }
        }
        if (Triggers.Count == 0) errors.Add("開始条件がありません。");
        var hotkeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { StopKey ?? "" };
        foreach (var t in Triggers)
        {
            if (t == null) { errors.Add("開始条件が空です。"); continue; }
            if (!new[] { "immediate", "at", "interval", "daily", "hotkey", "application" }.Contains(t.Type)) errors.Add("未対応の開始条件です。");
            if (t.Type == "at" && !ReplayTriggerClock.TryOffsetTime(t.At, out _)) errors.Add("atにはUTCまたはoffset付き日時が必要です。");
            if (t.LateGraceSeconds < 0 || t.LateGraceSeconds > 86400) errors.Add("遅刻許容幅が範囲外です。");
            if (t.Type == "interval" && (t.IntervalSeconds < 1 || t.IntervalSeconds > 86400)) errors.Add("intervalSecondsは1～86400です。");
            if (t.Type == "daily")
            {
                if (!TimeSpan.TryParseExact(t.Time, "hh\\:mm", CultureInfo.InvariantCulture, out _)) errors.Add("dailyのtimeはHH:mmです。");
                try { TimeZoneInfo.FindSystemTimeZoneById(t.TimeZone ?? ""); } catch { errors.Add("dailyのtimeZoneが不正です。"); }
            }
            if (t.Type == "hotkey")
            {
                try { ReplayKeys.Parse(t.Key, true); } catch { errors.Add("開始キーが不正です。"); }
                if (!hotkeys.Add(t.Key ?? "")) errors.Add("開始キーと非常停止キーが重複しています。");
            }
            if (t.Type == "application" && (t.Target == null || !Targets.ContainsKey(t.Target))) errors.Add("applicationのtargetがありません。");
        }
        if (!string.IsNullOrEmpty(SourceDirectory) && checkFiles)
        {
            try { if (!string.Equals(SourceSha256, ReplayImporter.SourceHash(PathOf(SourceDirectory)), StringComparison.OrdinalIgnoreCase)) errors.Add("元の記録が変更されています。configを作り直してください。"); }
            catch (Exception) { errors.Add("元の記録を確認できません。"); }
        }
        return errors;
    }
}
public sealed class ReplayTarget
{
    public string Executable { get; set; } = "";
    public string Title { get; set; } = "";
    public string ClassPrefix { get; set; } = "";
    public int ProcessId { get; set; }
    public int RecordedWidth { get; set; }
    public int RecordedHeight { get; set; }
    public int RecordedDpi { get; set; }
    public bool AllowDpiChange { get; set; }
    public double MaxAspectChange { get; set; } = .1;
}
public sealed class ReplayElement
{
    public string AutomationId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ControlType { get; set; } = "";
    public string ClassPrefix { get; set; } = "";
    [JsonIgnore] public bool IsSpecific => !string.IsNullOrWhiteSpace(AutomationId) || (!string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(ControlType));
}
public sealed class ReplayStep
{
    public long SourceSeq { get; set; }
    public string Kind { get; set; } = "wait";
    public string Target { get; set; }
    public ReplayElement Element { get; set; }
    public string PositionMode { get; set; } = "element";
    public double X { get; set; } = .5;
    public double Y { get; set; } = .5;
    public double EndX { get; set; } = .5;
    public double EndY { get; set; } = .5;
    public string Key { get; set; }
    public int WheelNotches { get; set; }
    public ReplayText Text { get; set; }
    public bool IsPassword { get; set; }
    public int DelayMs { get; set; }
    public int? TimeoutMs { get; set; }
    public ReplayImage BeforeImage { get; set; }
    public ReplayImage AfterImage { get; set; }
    public bool Skip { get; set; }
    public string SkipReason { get; set; }
    public string[] Arguments { get; set; } = Array.Empty<string>();
}
public sealed class ReplayText { public string Literal { get; set; } public string Environment { get; set; } }
public sealed class ReplayRect { public int X { get; set; } public int Y { get; set; } public int W { get; set; } public int H { get; set; } }
public sealed class ReplayImage { public string File { get; set; } public ReplayRect Crop { get; set; } public double Threshold { get; set; } = .98; }
public sealed class ReplayTrigger
{
    public string Type { get; set; } = "immediate";
    public string At { get; set; }
    public int IntervalSeconds { get; set; } = 60;
    public string Time { get; set; }
    public string TimeZone { get; set; } = "Tokyo Standard Time";
    public int LateGraceSeconds { get; set; } = 30;
    public string Key { get; set; }
    public string Target { get; set; }
}
public static class ReplayKeys
{
    static readonly Dictionary<string, int> Special = new(StringComparer.OrdinalIgnoreCase)
    { ["Enter"] = 13, ["Tab"] = 9, ["Esc"] = 27, ["Escape"] = 27, ["Space"] = 32, ["Backspace"] = 8,
      ["Left"] = 37, ["Up"] = 38, ["Right"] = 39, ["Down"] = 40, ["PrintScreen"] = 44 };
    public static int[] Parse(string text, bool hotkey = false)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("キーがありません。");
        var parts = text.Split('+'); var keys = new List<int>();
        foreach (var raw in parts)
        {
            var part = raw.Trim(); int key = part.ToUpperInvariant() switch { "CTRL" => 17, "SHIFT" => 16, "ALT" => 18, "WIN" => 91, _ => 0 };
            if (key != 0) { if (keys.Contains(key) || keys.Any(k => k != 16 && k != 17 && k != 18 && k != 91)) throw new ArgumentException("修飾キーの順序が不正です。"); }
            else { key = Special.TryGetValue(part, out var value) ? value : HotkeyKeys.Parse(part); if (key == 0 || keys.Any(k => k != 16 && k != 17 && k != 18 && k != 91)) throw new ArgumentException("不明なキーです。"); }
            keys.Add(key);
        }
        if (keys.Count == 0 || new[] { 16, 17, 18, 91 }.Contains(keys.Last()) || (keys.Contains(91) && keys.Last() == 76) || (keys.Contains(17) && keys.Contains(18) && keys.Last() == 46)) throw new ArgumentException("再現できないシステムキーです。");
        if (hotkey && !keys.Take(keys.Count - 1).Any(k => k == 17 || k == 18)) throw new ArgumentException("開始/停止にはCtrlかAltが必要です。");
        return keys.ToArray();
    }
}
