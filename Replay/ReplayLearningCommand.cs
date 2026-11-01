using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OperationRecorderApp.Replay;

public static class ReplayLearningCommand
{
    public const string Usage = "OperationRecorder.exe --replay learn-prepare --input <比較元JSON> --out <新規bundleフォルダー>\n" +
        "OperationRecorder.exe --replay learn-apply --bundle <bundle> --proposal <条件案JSON> --out <新規draft.json>\n" +
        "OperationRecorder.exe --replay learn-accept --config <確認したdraft.json> --out <新規config.json>";
    public static int Execute(string[] args)
    {
        CommandConsole.Prepare(); int code = 0;
        var result = new Dictionary<string, object> { ["schemaVersion"] = 1, ["product"] = "operation-recorder-replay-learning", ["status"] = "failed" };
        try
        {
            string command = args[1]; result["command"] = command;
            string[] allowed = command switch { "learn-prepare" => new[] { "--input", "--out" }, "learn-apply" => new[] { "--bundle", "--proposal", "--out" }, "learn-accept" => new[] { "--config", "--out" }, _ => throw new ArgumentException(Usage) };
            var fields = new Dictionary<string, string>();
            for (int i = 2; i < args.Length; i += 2)
                if (i + 1 >= args.Length || !allowed.Contains(args[i]) || args[i + 1].StartsWith("--") || !fields.TryAdd(args[i], args[i + 1])) throw new ArgumentException(Usage);
            if (allowed.Any(key => !fields.ContainsKey(key))) throw new ArgumentException(Usage);
            string output = Path.GetFullPath(fields["--out"]);
            if (command == "learn-prepare") { ReplayLearning.Prepare(fields["--input"], output); result["status"] = "prepared"; }
            else if (command == "learn-apply")
            {
                var config = ReplayLearning.Apply(fields["--bundle"], fields["--proposal"], output);
                result["status"] = "reviewRequired"; result["unresolved"] = config.LearningReview.Unresolved.Count;
            }
            else { ReplayLearning.Accept(fields["--config"], output); result["status"] = "accepted"; }
            result["output"] = output;
        }
        catch (Exception error)
        {
            code = error is ArgumentException or JsonException ? 2 : 1;
            result["errorType"] = error.GetType().Name;
            result["message"] = error is ArgumentException ? error.Message : "分析用ファイルを処理できませんでした。保存先とJSONを確認してください。";
        }
        result["exitCode"] = code; Console.WriteLine(JsonSerializer.Serialize(result, ReplayConfig.JsonOptions)); return code;
    }
}
