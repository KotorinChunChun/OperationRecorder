using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpRec;

namespace OperationRecorderApp;

internal sealed record SkillMask(int X, int Y, int Width, int Height);
internal sealed record SkillImage(string Side, byte[] Png);
internal sealed record SkillEvidence(long Seq, long AtMs, string Action, string Narration, List<SkillImage> Images);
internal sealed record SkillReview(List<SkillEvidence> Steps);
internal sealed record SkillInstruction(string Instruction, List<long> Evidence);
internal sealed record SkillProcedure(string Goal, string UseWhen, string Prerequisites, string Parameters,
    string Completion, List<SkillInstruction> Steps);
internal sealed record SkillDraft(SkillReview Review, SkillProcedure Procedure);

internal sealed class SkillSelection
{
    public long Seq { get; set; }
    public long AtMs { get; set; }
    public bool Include { get; set; } = true;
    public string Action { get; set; }
    public string Narration { get; set; }
    public bool IncludeBefore { get; set; }
    public bool IncludeAfter { get; set; }
    public List<SkillMask> BeforeMasks { get; set; } = [];
    public List<SkillMask> AfterMasks { get; set; } = [];
}

internal static class SkillDraftFiles
{
    public const int MaxBytes = 16 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static T Read<T>(string path)
    {
        AutomationWorkspace.CheckLinks(path);
        using var input = File.OpenRead(path);
        if (input.Length > MaxBytes) throw new IOException("分析用ファイルが大きすぎます。");
        return JsonSerializer.Deserialize<T>(input, Json) ?? throw new IOException("分析用ファイルが空です。");
    }

    public static void WriteNew(string path, string text)
    {
        path = Path.GetFullPath(path);
        AutomationWorkspace.CheckLinks(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: false);
        }
        finally { File.Delete(temporary); }
    }

    public static void WriteJson<T>(string path, T data)
    {
        string text = JsonSerializer.Serialize(data, Json);
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new IOException("分析用ファイルが大きすぎます。");
        WriteNew(path, text);
    }

    public static void OutsideRecording(string path, string session)
    {
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(session));
        string output = Path.GetFullPath(path);
        if (string.Equals(output, source, StringComparison.OrdinalIgnoreCase) ||
            output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("記録フォルダーの外に保存してください。");
    }
}

internal sealed class SkillSource
{
    readonly Dictionary<long, ReportGenerator.StepInfo> steps;
    readonly string session;

    public SkillSource(string session)
    {
        this.session = Path.GetFullPath(session);
        foreach (string name in new[] { "session.json", "events.jsonl", "edits.json", "transcript.json" })
        {
            string path = Path.Combine(this.session, name);
            AutomationWorkspace.CheckLinks(path);
            if (File.Exists(path) && new FileInfo(path).Length > SkillDraftFiles.MaxBytes)
                throw new IOException("元の記録が分析機能の上限を超えています。");
        }
        steps = ReportGenerator.ListSteps(this.session, restrictImagePaths: true).ToDictionary(s => s.Seq);
        if (steps.Count == 0) throw new ArgumentException("分析する操作が記録されていません。");
    }

    public List<SkillSelection> SelectSteps() => steps.Values.Select(step => new SkillSelection
    {
        Seq = step.Seq, AtMs = step.StartMs,
        Action = string.Join("\n", new[] { string.IsNullOrWhiteSpace(step.Headline) ? "画面を記録する" : step.Headline, step.EditedBody }
            .Where(text => !string.IsNullOrWhiteSpace(text))),
        Narration = step.Narration ?? "",
    }).ToList();

    public Bitmap Image(long seq, string side, List<SkillMask> masks)
    {
        var step = steps[seq];
        string path = side == "before" ? step.BeforePath : step.AfterPath;
        return File.Exists(path) ? SkillDrafts.MaskImage(session, path, masks) : null;
    }

    public SkillReview Prepare(IEnumerable<SkillSelection> choices)
    {
        var output = new List<SkillEvidence>();
        var seen = new HashSet<long>();
        long imageBytes = 0;
        foreach (var item in choices)
        {
            if (item == null) throw new ArgumentException("選択JSONに空の操作があります。");
            if (!item.Include) continue;
            if (!steps.TryGetValue(item.Seq, out var step) || !seen.Add(item.Seq))
                throw new ArgumentException("記録にない操作番号、または重複した操作番号があります。");
            var images = new List<SkillImage>();
            foreach (var (enabled, side, masks) in new[] { (item.IncludeBefore, "before", item.BeforeMasks), (item.IncludeAfter, "after", item.AfterMasks) })
            {
                if (!enabled) continue;
                using var image = Image(item.Seq, side, masks) ?? throw new ArgumentException($"操作 {item.Seq} に選択した画像がありません。");
                using var buffer = new MemoryStream();
                image.Save(buffer, ImageFormat.Png);
                imageBytes += buffer.Length;
                if (imageBytes > 9 * 1024 * 1024) throw new ArgumentException("送信する画像が大きすぎます。画像の数を減らしてください。");
                images.Add(new SkillImage(side, buffer.ToArray()));
            }
            output.Add(new SkillEvidence(item.Seq, step.StartMs, item.Action?.Trim() ?? "", item.Narration?.Trim() ?? "", images));
        }
        var result = new SkillReview(output);
        SkillDrafts.ValidateReview(result);
        return result;
    }
}

internal static class SkillDrafts
{
    public static Bitmap MaskImage(string root, string path, List<SkillMask> masks)
    {
        if (path == null) return null;
        path = Path.GetFullPath(path);
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("記録フォルダー内のPNGを選択してください。");
        AutomationWorkspace.CheckLinks(path);
        using var stream = File.OpenRead(path);
        if (stream.Length > SkillDraftFiles.MaxBytes) throw new IOException("画像が大きすぎます。");
        using var original = System.Drawing.Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
        if (original.Width > 8192 || original.Height > 8192 || (long)original.Width * original.Height > 34_000_000)
            throw new IOException("画像の寸法が大きすぎます。");
        var result = new Bitmap(original.Width, original.Height, PixelFormat.Format24bppRgb);
        try
        {
            using var canvas = Graphics.FromImage(result);
            canvas.DrawImageUnscaled(original, 0, 0);
            foreach (var mask in masks ?? [])
            {
                if (mask == null || mask.X < 0 || mask.Y < 0 || mask.Width <= 0 || mask.Height <= 0 ||
                    mask.Width > result.Width - mask.X || mask.Height > result.Height - mask.Y)
                    throw new ArgumentException("マスクの範囲が画像からはみ出しています。");
                canvas.FillRectangle(Brushes.Black, mask.X, mask.Y, mask.Width, mask.Height);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public static void ValidateReview(SkillReview review)
    {
        if (review?.Steps == null || review.Steps.Count is < 1 or > 300 ||
            review.Steps.Any(s => s == null || s.Seq <= 0 || s.AtMs < 0 ||
                !Text(s.Action, 4000) || !Text(s.Narration, 8000, empty: true) || s.Images == null || s.Images.Count > 2 ||
                s.Images.Any(i => i == null || i.Side is not ("before" or "after") || !IsPng(i.Png)) ||
                s.Images.Select(i => i.Side).Distinct().Count() != s.Images.Count) ||
            review.Steps.Select(s => s.Seq).Distinct().Count() != review.Steps.Count ||
            review.Steps.Sum(s => s.Images.Sum(i => (long)i.Png.Length)) > 9 * 1024 * 1024 ||
            review.Steps.Sum(s => Encoding.UTF8.GetByteCount(s.Action) + Encoding.UTF8.GetByteCount(s.Narration)) > 300_000)
            throw new ArgumentException("分析対象の操作、文章、画像の形式と件数を確認してください。");
    }

    static bool IsPng(byte[] bytes)
    {
        ReadOnlySpan<byte> header = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82];
        if (bytes == null || bytes.Length < 33 || !bytes.AsSpan().StartsWith(header)) return false;
        int width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        return width is > 0 and <= 8192 && height is > 0 and <= 8192 && (long)width * height <= 34_000_000;
    }

    public static void Validate(SkillDraft draft)
    {
        ValidateReview(draft?.Review);
        var plan = draft.Procedure;
        var evidence = draft.Review.Steps.Select(s => s.Seq).ToHashSet();
        if (plan == null || !Text(plan.Goal, 500) || !Text(plan.UseWhen, 1000) ||
            !Text(plan.Prerequisites, 4000, empty: true) || !Text(plan.Parameters, 4000, empty: true) ||
            !Text(plan.Completion, 4000) || plan.Steps == null || plan.Steps.Count is < 1 or > 300 ||
            plan.Steps.Any(s => s == null || !Text(s.Instruction, 4000) || s.Evidence == null ||
                s.Evidence.Count == 0 || s.Evidence.Count > evidence.Count || s.Evidence.Any(seq => !evidence.Contains(seq))))
            throw new ArgumentException("手順案の目的、使用条件、完了条件、各手順の文章と根拠番号を確認してください。");
    }

    static bool Text(string value, int max, bool empty = false) =>
        value != null && value.Length <= max && (empty || !string.IsNullOrWhiteSpace(value));

    public static string RenderSkill(SkillDraft draft, string name)
    {
        Validate(draft);
        if (name == null || name.Length > 64 || !Regex.IsMatch(name, @"\A[a-z0-9]+(?:-[a-z0-9]+)*\z"))
            throw new ArgumentException("スキル名は64文字以内の小文字英数字と区切りのハイフンで指定してください。");
        var plan = draft.Procedure;
        string description = Regex.Replace(plan.UseWhen.Trim(), @"\s+", " ");
        var text = new StringBuilder("---\nname: ").Append(name).Append("\ndescription: ")
            .Append(JsonSerializer.Serialize(description, SkillDraftFiles.Json)).Append("\n---\n\n");
        RenderProcedure(text, plan, value => value);
        foreach (var (step, index) in plan.Steps.Select((step, index) => (step, index)))
            text.Append(index + 1).Append(". ").AppendLine(Indent(step.Instruction, "   "));
        Section(text, "完了条件", plan.Completion);
        return text.ToString();
    }

    public static string RenderManual(SkillDraft draft)
    {
        Validate(draft);
        var text = new StringBuilder();
        RenderProcedure(text, draft.Procedure, ManualText);
        var sources = draft.Review.Steps.ToDictionary(s => s.Seq);
        var shownImages = new HashSet<long>();
        int index = 0;
        foreach (var step in draft.Procedure.Steps)
        {
            text.Append("### 手順 ").AppendLine((++index).ToString()).AppendLine()
                .AppendLine(ManualText(step.Instruction.Trim())).AppendLine();
            foreach (var seq in step.Evidence.Distinct())
            {
                var source = sources[seq];
                text.Append("根拠の操作 ").Append(seq).Append("（").Append(source.AtMs).AppendLine(" ms）").AppendLine();
                if (!shownImages.Add(seq)) continue;
                foreach (var image in source.Images)
                    text.Append("![").Append(image.Side == "before" ? "操作前" : "操作後")
                        .Append("](data:image/png;base64,").Append(Convert.ToBase64String(image.Png)).AppendLine(")").AppendLine();
            }
        }
        Section(text, "完了条件", ManualText(draft.Procedure.Completion));
        return text.ToString();
    }

    static void RenderProcedure(StringBuilder text, SkillProcedure plan, Func<string, string> format)
    {
        text.Append("# ").AppendLine(format(Regex.Replace(plan.Goal.Trim(), @"\s+", " "))).AppendLine();
        Section(text, "使用条件", format(plan.UseWhen));
        Section(text, "前提", format(plan.Prerequisites));
        Section(text, "可変項目", format(plan.Parameters));
        text.AppendLine("## 手順").AppendLine();
    }

    static void Section(StringBuilder text, string title, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) text.Append("## ").AppendLine(title).AppendLine().AppendLine(value.Trim()).AppendLine();
    }

    static string Indent(string value, string spaces) => value.Trim().Replace("\r\n", "\n").Replace("\n", "\n" + spaces);

    static string ManualText(string value) => string.Join("\n", value.Replace("\r\n", "\n").Split('\n')
        .Select(SessionTranscript.EscapeMarkdown));
}
