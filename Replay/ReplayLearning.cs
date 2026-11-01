using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OperationRecorderApp.Replay;

public sealed class ReplayLearningRequest
{
    public int SchemaVersion { get; set; } = 1;
    public List<string> Sources { get; set; } = new();
}
public sealed class ReplayLearningRecording : ReplayLearningSource
{
    public List<ReplayLearningObservation> Steps { get; set; } = new();
}
public class ReplayLearningSource
{
    public string RecordingId { get; set; }
    public string Directory { get; set; }
    public string Sha256 { get; set; }
}
public sealed class ReplayLearningObservation
{
    public int Step { get; set; }
    public long SourceSeq { get; set; }
    public string Kind { get; set; }
    public string ExecutableName { get; set; }
    public Dictionary<string, string> Window { get; set; }
    public Dictionary<string, string> Element { get; set; }
}
public sealed class ReplayLearningManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string BundleId { get; set; }
    public string ObservationsSha256 { get; set; }
    public List<ReplayLearningSource> Sources { get; set; } = new();
}
public sealed class ReplayLearningEvidence
{
    public string RecordingId { get; set; }
    public long SourceSeq { get; set; }
}
public sealed class ReplayLearningStep
{
    public int Step { get; set; }
    public List<ReplayLearningEvidence> Correspondences { get; set; } = new();
    public ReplayCondition WindowCondition { get; set; }
    public ReplayCondition ElementCondition { get; set; }
    public string Reason { get; set; }
    public List<string> DynamicParts { get; set; } = new();
    public List<string> Risks { get; set; } = new();
}
public sealed class ReplayLearningProposal
{
    public int SchemaVersion { get; set; } = 1;
    public string BundleId { get; set; }
    public string ObservationsSha256 { get; set; }
    public string Summary { get; set; }
    public List<ReplayLearningStep> Steps { get; set; } = new();
    public List<string> Questions { get; set; } = new();
}

public static class ReplayLearning
{
    static readonly UTF8Encoding Utf8 = new(false);
    public static T Read<T>(string file, int maxBytes = 8 * 1024 * 1024)
    {
        AutomationWorkspace.CheckLinks(Path.GetFullPath(file));
        if (new FileInfo(file).Length > maxBytes) throw new ArgumentException("分析用JSONが上限を超えています。");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(file), ReplayConfig.JsonOptions) ?? throw new ArgumentException("分析用JSONが空です。");
    }
    static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ReplayConfig.JsonOptions), ReplayConfig.JsonOptions);
    static void Write(string file, object value) => File.WriteAllText(file, JsonSerializer.Serialize(value, ReplayConfig.JsonOptions), Utf8);
    static void NewFile(string file)
    {
        AutomationWorkspace.CheckLinks(file);
        if (File.Exists(file) || Directory.Exists(file)) throw new ArgumentException("既存ファイルを上書きできません。新しい保存先を指定してください。");
    }
    static bool Within(string path, string root) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    static void SaveNew(ReplayConfig config, string file)
    {
        file = Path.GetFullPath(file); NewFile(file);
        foreach (var source in config.Sources) if (Within(file, config.PathOf(source.Directory))) throw new ArgumentException("原本の記録内へ分析結果を書き込めません。");
        config.Save(file);
    }
    static void AbsolutePaths(ReplayConfig config)
    {
        if (!string.IsNullOrEmpty(config.SourceDirectory)) config.SourceDirectory = config.PathOf(config.SourceDirectory);
        foreach (var source in config.Sources) source.Directory = config.PathOf(source.Directory);
        foreach (var image in config.Steps.SelectMany(s => new[] { s.BeforeImage, s.AfterImage }).Where(g => g != null)) image.File = config.PathOf(image.File);
    }
    static List<ReplayLearningRecording> Observe(IReadOnlyList<ReplayLearningSource> sources)
    {
        var result = new List<ReplayLearningRecording>();
        foreach (var source in sources)
        {
            AutomationWorkspace.CheckLinks(source.Directory);
            AutomationWorkspace.CheckLinks(Path.Combine(source.Directory, "session.json"));
            AutomationWorkspace.CheckLinks(Path.Combine(source.Directory, "events.jsonl"));
            if (ReplayImporter.SourceHash(source.Directory) != source.Sha256) throw new ArgumentException("比較元の記録が変更されています。bundleを作り直してください。");
            var config = ReplayImporter.Import(source.Directory);
            if (config.Steps.Count == 0 || config.Steps.Count > 2000) throw new ArgumentException("各記録は1～2000手順で指定してください。");
            var events = File.ReadLines(Path.Combine(source.Directory, "events.jsonl")).Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => JsonNode.Parse(s).AsObject()).ToDictionary(e => e["seq"]?.GetValue<long>() ?? 0);
            var recording = new ReplayLearningRecording { RecordingId = source.RecordingId, Sha256 = source.Sha256 };
            // Directoryは共有データに書かない。DTOからも明示して除外する。
            for (int index = 0; index < config.Steps.Count; index++)
            {
                var step = config.Steps[index]; var data = events[step.SourceSeq]; var window = data["window"]; var element = data["uia"];
                string Text(JsonNode value) => value?.GetValue<string>() ?? "";
                var sample = new ReplayLearningObservation
                {
                    Step = index + 1, SourceSeq = step.SourceSeq, Kind = step.Kind,
                    ExecutableName = Path.GetFileName(config.Targets[step.Target].Executable),
                    Window = ReplayCondition.Window(Text(window?["title"]), Text(window?["className"])),
                    Element = element == null ? null : ReplayCondition.Element(Text(element["automationId"]), Text(element["name"]), Text(element["className"]), Text(element["controlType"])),
                };
                if (sample.Window.Values.Concat(sample.Element?.Values ?? Enumerable.Empty<string>()).Any(s => s.Length > 2048)) throw new ArgumentException("比較対象のタイトル/要素名が長すぎます。記録を分割してください。");
                recording.Steps.Add(sample);
            }
            if (ReplayImporter.SourceHash(source.Directory) != source.Sha256) throw new ArgumentException("分析準備中に記録が変更されました。");
            result.Add(recording);
        }
        return result;
    }
    public static string Prepare(string requestFile, string outputDirectory)
    {
        requestFile = Path.GetFullPath(requestFile); var request = Read<ReplayLearningRequest>(requestFile);
        if (request.SchemaVersion != 1 || request.Sources == null || request.Sources.Count < 2 || request.Sources.Count > 20) throw new ArgumentException("schemaVersion=1、比較元のsourcesは2～20記録が必要です。");
        var directories = request.Sources.Select(s => Path.GetFullPath(s, Path.GetDirectoryName(requestFile))).ToArray();
        if (directories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != directories.Length) throw new ArgumentException("同じ記録を複数回指定できません。");
        outputDirectory = Path.GetFullPath(outputDirectory); NewFile(outputDirectory);
        if (directories.Any(s => Within(outputDirectory, s))) throw new ArgumentException("原本の記録内にbundleを作成できません。");
        var manifest = new ReplayLearningManifest { BundleId = Guid.NewGuid().ToString("N") };
        for (int i = 0; i < directories.Length; i++) manifest.Sources.Add(new() { RecordingId = "recording" + (i + 1), Directory = directories[i], Sha256 = ReplayImporter.SourceHash(directories[i]) });
        var recordings = Observe(manifest.Sources);
        var share = new { schemaVersion = 1, bundleId = manifest.BundleId, baselineRecordingId = "recording1",
            recordings = recordings.Select(r => new { r.RecordingId, r.Sha256, r.Steps }) };
        string json = JsonSerializer.Serialize(share, ReplayConfig.JsonOptions);
        if (Utf8.GetByteCount(json) > 8 * 1024 * 1024) throw new ArgumentException("比較データが8MBを超えています。記録数/手順を減らしてください。");
        var template = Path.Combine(AppContext.BaseDirectory, "config", "replay-learning-prompt.md");
        var schemaFile = Path.Combine(AppContext.BaseDirectory, "config", "replay-learning-response.schema.json");
        string prompt = File.ReadAllText(template); var schema = JsonNode.Parse(File.ReadAllText(schemaFile));
        manifest.ObservationsSha256 = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(json)));
        schema["properties"]["bundleId"]["const"] = manifest.BundleId;
        schema["properties"]["observationsSha256"]["const"] = manifest.ObservationsSha256;
        prompt = prompt.Replace("{{BUNDLE_ID}}", manifest.BundleId).Replace("{{OBSERVATIONS_SHA256}}", manifest.ObservationsSha256);
        string analysis = Path.Combine(outputDirectory, "analysis"); Directory.CreateDirectory(analysis);
        File.WriteAllText(Path.Combine(analysis, "observations.json"), json, Utf8);
        File.WriteAllText(Path.Combine(analysis, "prompt.md"), prompt, Utf8);
        File.WriteAllText(Path.Combine(analysis, "response.schema.json"), schema.ToJsonString(ReplayConfig.JsonOptions), Utf8);
        Write(Path.Combine(outputDirectory, "manifest.json"), manifest);
        return outputDirectory;
    }
    static ReplayLearningManifest Manifest(string bundle)
    {
        var manifest = Read<ReplayLearningManifest>(Path.Combine(bundle, "manifest.json"), 65536);
        if (manifest.SchemaVersion != 1 || !Guid.TryParseExact(manifest.BundleId, "N", out _) || manifest.Sources == null || manifest.Sources.Count < 2 || manifest.Sources.Count > 20 ||
            manifest.Sources.Any(s => s == null || string.IsNullOrEmpty(s.RecordingId) || !Path.IsPathFullyQualified(s.Directory)) ||
            manifest.Sources.Select(s => s.RecordingId).Distinct().Count() != manifest.Sources.Count ||
            manifest.Sources.Select(s => s.Directory).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Sources.Count)
            throw new ArgumentException("bundleの所有情報が不正です。");
        string observations = Path.Combine(bundle, "analysis", "observations.json"); AutomationWorkspace.CheckLinks(observations);
        if (Hash(observations) != manifest.ObservationsSha256) throw new ArgumentException("比較データが変更されています。bundleを作り直してください。");
        return manifest;
    }
    public static ReplayConfig Apply(string bundle, string proposalFile, string outputFile)
    {
        bundle = Path.GetFullPath(bundle); proposalFile = Path.GetFullPath(proposalFile);
        outputFile = Path.GetFullPath(outputFile); NewFile(outputFile); NewFile(outputFile + ".review.json");
        var manifest = Manifest(bundle); var proposal = Read<ReplayLearningProposal>(proposalFile, 2 * 1024 * 1024);
        if (proposal.SchemaVersion != 1 || proposal.BundleId != manifest.BundleId || proposal.ObservationsSha256 != manifest.ObservationsSha256 || proposal.Steps == null || proposal.Questions == null || string.IsNullOrWhiteSpace(proposal.Summary)) throw new ArgumentException("条件案の版・bundle・比較データ・必須項目が一致しません。");
        var recordings = Observe(manifest.Sources); var config = ReplayImporter.Import(manifest.Sources[0].Directory);
        if (proposal.Steps.Count != config.Steps.Count || proposal.Steps.Select(s => s?.Step ?? 0).Distinct().Count() != config.Steps.Count || proposal.Steps.Any(s => s == null || s.Step < 1 || s.Step > config.Steps.Count)) throw new ArgumentException("条件案には基準記録の全手順を一度ずつ指定してください。");
        config.Sources = manifest.Sources.Select(s => new ReplaySource { Directory = s.Directory, Sha256 = s.Sha256 }).ToList();
        config.LearningReview = new() { BundleId = manifest.BundleId, ProposalSha256 = Hash(proposalFile), Unresolved = new(proposal.Questions) };
        var issues = config.LearningReview.Unresolved; var used = new HashSet<string>(); var warnings = new List<string>();
        var lastCorrespondence = new Dictionary<string, int>();
        foreach (var change in proposal.Steps.OrderBy(s => s.Step))
        {
            var step = config.Steps[change.Step - 1]; var baseline = recordings[0].Steps[change.Step - 1];
            if (change.Correspondences == null || change.DynamicParts == null || change.Risks == null || string.IsNullOrWhiteSpace(change.Reason) || change.WindowCondition == null) throw new ArgumentException("各手順の根拠・対応付け・ウィンドウ条件が必要です。");
            var conditionErrors = change.WindowCondition.Validate(true).Concat(change.ElementCondition?.Validate(false) ?? Enumerable.Empty<string>()).ToArray();
            if (conditionErrors.Length > 0) throw new ArgumentException("手順 " + change.Step + ": " + string.Join(" / ", conditionErrors));
            var distinct = change.Correspondences.Select(e => e?.RecordingId).Distinct().Count();
            if (distinct < 2 || distinct != change.Correspondences.Count) issues.Add("手順 " + change.Step + ": 2記録以上の重複しない対応付けが必要です。");
            if (!change.Correspondences.Any(e => e?.RecordingId == recordings[0].RecordingId && e.SourceSeq == baseline.SourceSeq)) issues.Add("手順 " + change.Step + ": 基準手順の対応がありません。");
            foreach (var evidence in change.Correspondences)
            {
                var recording = recordings.SingleOrDefault(r => r.RecordingId == evidence?.RecordingId);
                var sample = recording?.Steps.SingleOrDefault(s => s.SourceSeq == evidence.SourceSeq);
                if (sample == null || sample.Kind != step.Kind || !string.Equals(sample.ExecutableName, baseline.ExecutableName, StringComparison.OrdinalIgnoreCase)) { issues.Add("手順 " + change.Step + ": 対応手順/操作種別/EXEが一致しません。"); continue; }
                used.Add(recording.RecordingId);
                if (lastCorrespondence.TryGetValue(recording.RecordingId, out int previous) && sample.Step <= previous) issues.Add("手順 " + change.Step + ": 対応付けが逆順または同じイベントを重複使用しています。");
                lastCorrespondence[recording.RecordingId] = sample.Step;
                if (change.ElementCondition != null && baseline.Element?.GetValueOrDefault("controlType") != sample.Element?.GetValueOrDefault("controlType")) issues.Add("手順 " + change.Step + ": UIAの種類が基準と一致しません。");
                if (!change.WindowCondition.Matches(sample.Window) || (change.ElementCondition != null && (sample.Element == null || !change.ElementCondition.Matches(sample.Element)))) issues.Add("手順 " + change.Step + ": 条件が対応する実記録の全例へ一致しません。");
                if (change.ElementCondition != null && sample.Element != null && recording.Steps.Any(other => other.SourceSeq != sample.SourceSeq && other.Element != null &&
                    other.ExecutableName == sample.ExecutableName && !other.Element.OrderBy(p => p.Key).SequenceEqual(sample.Element.OrderBy(p => p.Key)) && change.ElementCondition.Matches(other.Element)))
                    warnings.Add("手順 " + change.Step + ": 同じ記録の別要素にも一致します。同時候補の有無を確認してください。");
            }
            string binding = "learned-" + change.Step.ToString("D4");
            var target = Clone(config.Targets[step.Target]); target.Title = target.ClassPrefix = ""; target.ProcessId = 0; target.Condition = change.WindowCondition;
            config.Targets[binding] = target; step.Target = binding;
            if (change.ElementCondition != null)
            {
                step.Element = new() { ControlType = baseline.Element?.GetValueOrDefault("controlType") ?? "", Condition = change.ElementCondition };
                if (!step.Element.IsSpecific) issues.Add("手順 " + change.Step + ": 名前/AutomationIdとControlTypeで要素を特定してください。");
            }
        }
        if (used.Count != recordings.Count) issues.Add("対応付けに使用されていない比較元の記録があります。");
        config.Targets = config.Targets.Where(t => config.Steps.Any(s => s.Target == t.Key)).ToDictionary(t => t.Key, t => t.Value);
        issues.AddRange(config.Validate(allowUnreviewed: true).Where(e => !e.StartsWith("AI条件案に未解決")));
        config.LearningReview.Unresolved = issues.Distinct().ToList();
        SaveNew(config, outputFile);
        // 根拠と注意点はconfigと分ける。入力値や原本の絶対パスは報告へ書かない。
        Write(Path.GetFullPath(outputFile) + ".review.json", new { proposal.BundleId, proposal.Summary, steps = proposal.Steps, warnings = warnings.Distinct(), unresolved = config.LearningReview.Unresolved, requiresReview = true });
        return config;
    }
    public static void Accept(string configFile, string outputFile)
    {
        var config = ReplayConfig.Load(configFile);
        if (config.LearningReview == null) throw new ArgumentException("AI条件案の確認情報がありません。");
        AbsolutePaths(config);
        var errors = config.Validate(checkEnvironment: true, allowUnreviewed: true);
        if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
        config.LearningReview.Accepted = true; config.LearningReview.AcceptedConfigSha256 = config.ReviewHash();
        SaveNew(config, outputFile);
    }
}
