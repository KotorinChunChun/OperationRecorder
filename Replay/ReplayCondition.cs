using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace OperationRecorderApp.Replay;

// 条件はデータとして評価する。C#/PowerShell等への変換やevalはしない。
public sealed class ReplayCondition
{
    public List<ReplayPredicate> All { get; set; } = new();
    public List<ReplayPredicate> Any { get; set; } = new();
    public List<ReplayPredicate> None { get; set; } = new();
    public IEnumerable<ReplayPredicate> Positive => (All ?? new()).Concat(Any ?? new());
    public bool Matches(IReadOnlyDictionary<string, string> fields) =>
        All.All(p => p.Matches(fields)) && (Any.Count == 0 || Any.Any(p => p.Matches(fields))) && !None.Any(p => p.Matches(fields));
    public IEnumerable<string> Validate(bool window)
    {
        if (All == null || Any == null || None == null) { yield return "all/any/noneは配列が必要です。"; yield break; }
        if (All.Count + Any.Count == 0 || All.Count + Any.Count + None.Count > 32) yield return "正の条件が必要で、条件は合計32件までです。";
        foreach (var p in All.Concat(Any).Concat(None))
        {
            if (p == null) { yield return "空の条件は使用できません。"; continue; }
            var allowed = window ? new[] { "title", "className" } : new[] { "automationId", "name", "className", "controlType" };
            if (!allowed.Contains(p.Field)) yield return "条件のfieldが対象範囲外です。";
            if (!new[] { "exact", "contains", "prefix", "regex" }.Contains(p.Match) || string.IsNullOrEmpty(p.Value) || p.Value.Length > 512) yield return "条件の比較方式/値が不正です（値は1～512文字）。";
            if (p.Match == "regex")
            {
                bool valid;
                try { _ = ReplayPredicate.Pattern(p.Value, p.IgnoreCase); valid = true; } catch (Exception e) when (e is ArgumentException or NotSupportedException) { valid = false; }
                if (!valid) yield return "正規表現が不正またはNonBacktracking非対応です。";
            }
        }
    }
    public static Dictionary<string, string> Window(string title, string className) => new() { ["title"] = title ?? "", ["className"] = className ?? "" };
    public static Dictionary<string, string> Element(string id, string name, string className, string type) => new()
    { ["automationId"] = id ?? "", ["name"] = name ?? "", ["className"] = className ?? "", ["controlType"] = (type ?? "").Replace("ControlType.", "") };
}
public sealed class ReplayPredicate
{
    public string Field { get; set; }
    public string Match { get; set; } = "exact";
    public string Value { get; set; }
    public bool IgnoreCase { get; set; }
    internal static Regex Pattern(string value, bool ignoreCase) => new("\\A(?:" + value + ")\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromMilliseconds(100));
    public bool Matches(IReadOnlyDictionary<string, string> fields)
    {
        if (Field == null || !fields.TryGetValue(Field, out var actual) || actual.Length > 8192) return false;
        var comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Match switch
        {
            "exact" => string.Equals(actual, Value, comparison),
            "contains" => actual.Contains(Value, comparison),
            "prefix" => actual.StartsWith(Value, comparison),
            "regex" => Pattern(Value, IgnoreCase).IsMatch(actual),
            _ => throw new InvalidOperationException("未対応の条件比較です。"),
        };
    }
}
public sealed class ReplaySource
{
    public string Directory { get; set; }
    public string Sha256 { get; set; }
}
public sealed class ReplayLearningReview
{
    public bool Accepted { get; set; }
    public string BundleId { get; set; }
    public string ProposalSha256 { get; set; }
    public string AcceptedConfigSha256 { get; set; }
    public List<string> Unresolved { get; set; } = new();
}
