using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OperationRecorderApp
{
    public static class AutomationCommand
    {
        public const string Usage = "OperationRecorder.exe --automation composition|session|template|settings --input <JSON> --workspace <専用フォルダー> [--result <results配下のJSON>]";
        public static bool IsAutomation(string[] args) => args.Length > 0 && args[0] == "--automation";

        public static int Execute(string[] args)
        {
            CommandConsole.Prepare();
            var report = new JsonObject { ["schemaVersion"] = 1, ["success"] = false };
            AutomationWorkspace workspace = null;
            string resultFile = null;
            bool resultValidated = false;
            int code = 0;
            try
            {
                if (args.Length == 2 && args[1] == "help") { Console.WriteLine(Usage); return 0; }
                if (args.Length < 2 || !new[] { "composition", "session", "template", "settings" }.Contains(args[1])) throw new ArgumentException(Usage);
                string input = null, root = null;
                var used = new System.Collections.Generic.HashSet<string>();
                for (int index = 2; index < args.Length; index += 2)
                {
                    if (index + 1 >= args.Length || !used.Add(args[index])) throw new ArgumentException("引数の値不足または重複です。");
                    switch (args[index])
                    {
                        case "--input": input = args[index + 1]; break;
                        case "--workspace": root = args[index + 1]; break;
                        case "--result": resultFile = args[index + 1]; break;
                        default: throw new ArgumentException("不明な引数です：" + args[index]);
                    }
                }
                if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(root)) throw new ArgumentException(Usage);
                var inputPath = Path.GetFullPath(input); AutomationWorkspace.CheckLinks(inputPath);
                var request = BasicAutomation.ReadObject(inputPath);
                if (request["schemaVersion"] is not JsonValue schema || !schema.TryGetValue<int>(out var version) || version != 1)
                    throw new ArgumentException("schemaVersion=1 が必要です。");
                workspace = new AutomationWorkspace(root);
                if (resultFile != null)
                {
                    resultFile = workspace.Resolve(resultFile);
                    var resultsDirectory = workspace.Resolve("results");
                    if (!AutomationWorkspace.Within(resultFile, resultsDirectory) ||
                        string.Equals(resultFile, resultsDirectory, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(Path.GetExtension(resultFile), ".json", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("結果JSONは試験領域のresults配下へ保存してください。");
                    if (Directory.Exists(resultFile) || string.Equals(inputPath, resultFile, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("入力と結果の保存先を分けてください。");
                    if (File.Exists(resultFile))
                    {
                        var previousResult = BasicAutomation.ReadObject(resultFile);
                        if (previousResult["success"] is not JsonValue success || !success.TryGetValue<bool>(out _) ||
                            previousResult["exitCode"] is not JsonValue exit || !exit.TryGetValue<int>(out _))
                            throw new ArgumentException("既存の結果JSON以外を上書きできません。");
                    }
                    resultValidated = true;
                }
                report["area"] = args[1]; report["workspace"] = workspace.Root;
                report["result"] = args[1] == "composition" ? CompositionAutomation.Execute(request, workspace) : BasicAutomation.Execute(args[1], request, workspace);
                report["success"] = true;
            }
            catch (Exception error)
            {
                code = error is ArgumentException || error is JsonException ? 2 : 1;
                report["error"] = new JsonObject { ["type"] = error.GetType().Name, ["message"] = error.Message };
            }
            report["exitCode"] = code;
            if (workspace != null && resultFile != null && resultValidated)
            {
                try { workspace.WriteJson(resultFile, report); }
                catch (Exception error)
                {
                    code = 1; report["success"] = false; report["exitCode"] = code;
                    report["resultWriteError"] = error.Message;
                }
            }
            Console.WriteLine(report.ToJsonString());
            if (code != 0) Console.Error.WriteLine(report["error"]?["message"]?.ToString() ?? report["resultWriteError"]?.ToString());
            return code;
        }
    }
}
