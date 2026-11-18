using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace OperationRecorderApp;

internal static class SkillCommand
{
    const string Help = """
        OperationRecorder.exe --skill select --session <記録> --out <選択JSON>
        OperationRecorder.exe --skill prepare --session <記録> --selection <選択JSON> --out <送信確認JSON>
        OperationRecorder.exe --skill analyze --review <確認済みJSON> --endpoint <URL> --model <ID> --out <手順案JSON>
        OperationRecorder.exe --skill export --draft <確認済み手順案JSON> --name <スキル名> --out <SKILL.md>
        OperationRecorder.exe --skill manual --draft <確認済み手順案JSON> --out <手順書.md または 手順書.html>
        """;

    public static int Execute(string[] args)
    {
        CommandConsole.Prepare();
        if (args.Length == 1 || args[1] is "help" or "--help")
        {
            Console.WriteLine(Help);
            return 0;
        }
        try
        {
            string[] keys = args[1] switch
            {
                "select" => ["--session", "--out"],
                "prepare" => ["--session", "--selection", "--out"],
                "analyze" => ["--review", "--endpoint", "--model", "--out"],
                "export" => ["--draft", "--name", "--out"],
                "manual" => ["--draft", "--out"],
                _ => throw new ArgumentException(Help),
            };
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 2; i < args.Length; i += 2)
            {
                if (i + 1 == args.Length || !keys.Contains(args[i]) || args[i + 1].StartsWith("--", StringComparison.Ordinal) ||
                    !fields.TryAdd(args[i], args[i + 1])) throw new ArgumentException(Help);
            }

            if (fields.Count != keys.Length) throw new ArgumentException(Help);
            string output = fields["--out"];
            if (fields.TryGetValue("--session", out var session)) SkillDraftFiles.OutsideRecording(output, session);
            switch (args[1])
            {
                case "select":
                    SkillDraftFiles.WriteJson(output, new SkillSource(session).SelectSteps());
                    break;
                case "prepare":
                    var choices = SkillDraftFiles.Read<List<SkillSelection>>(fields["--selection"]);
                    SkillDraftFiles.WriteJson(output, new SkillSource(session).Prepare(choices));
                    break;
                case "analyze":
                    var review = SkillDraftFiles.Read<SkillReview>(fields["--review"]);
                    var endpoint = SkillAnalysis.Endpoint(fields["--endpoint"]);
                    var draft = SkillAnalysis.Generate(review, endpoint, fields["--model"],
                        Environment.GetEnvironmentVariable("OPREC_ANALYSIS_API_KEY"), CancellationToken.None).GetAwaiter().GetResult();
                    SkillDraftFiles.WriteJson(output, draft);
                    break;
                case "export":
                    if (!string.Equals(Path.GetFileName(output), "SKILL.md", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("出力ファイル名を SKILL.md にしてください。");
                    SkillDraftFiles.WriteNew(output, SkillDrafts.RenderSkill(SkillDraftFiles.Read<SkillDraft>(fields["--draft"]), fields["--name"]));
                    break;
                case "manual":
                    string markdown = SkillDrafts.RenderManual(SkillDraftFiles.Read<SkillDraft>(fields["--draft"]));
                    string extension = Path.GetExtension(output).ToLowerInvariant();
                    if (extension is not (".md" or ".html")) throw new ArgumentException("手順書の拡張子は .md または .html を指定してください。");
                    SkillDraftFiles.WriteNew(output, extension == ".html" ? HtmlExporter.ToHtml(markdown) : markdown);
                    break;
            }
            Console.WriteLine(Path.GetFullPath(output));
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or JsonException or TimeoutException)
        {
            Console.Error.WriteLine(error is JsonException ? "分析用JSONの形式を確認してください。" : error.Message);
            return 1;
        }
    }
}
