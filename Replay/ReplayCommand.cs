using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace OperationRecorderApp.Replay;

public static class ReplayCommand
{
    public const string Usage = "OperationRecorder.exe --replay import --source <記録フォルダー> --out <config.json> [--no-images]\n" +
        "OperationRecorder.exe --replay check|run|watch --config <config.json> [--now] [--logs <フォルダー>] [--result <結果.json>]";
    public static bool IsReplay(string[] args) => args.Length > 0 && args[0] == "--replay";
    public static int Execute(string[] args)
    {
        CommandConsole.Prepare(); int code = 0; string resultPath = null; string logDirectory = null;
        var result = new Dictionary<string, object> { ["schemaVersion"] = 1, ["product"] = "operation-recorder-replay", ["status"] = "failed" };
        try
        {
            if (args.Length == 2 && args[1] == "help") { Console.WriteLine(Usage); return 0; }
            if (args.Length < 2 || !new[] { "import", "check", "run", "watch" }.Contains(args[1])) throw new ArgumentException(Usage);
            string command = args[1]; result["command"] = command;
            var fields = new Dictionary<string, string>(); var switches = new HashSet<string>();
            for (int i = 2; i < args.Length; i++)
            {
                string key = args[i];
                if (key == "--now" || key == "--no-images") { if (!switches.Add(key)) throw new ArgumentException("引数が重複しています。"); }
                else if (new[] { "--source", "--out", "--config", "--logs", "--result" }.Contains(key) && i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                { if (!fields.TryAdd(key, args[++i])) throw new ArgumentException("引数が重複しています。"); }
                else throw new ArgumentException("引数の名前または値を確認してください。\n" + Usage);
            }
            string Required(string key) => fields.TryGetValue(key, out var value) ? value : throw new ArgumentException(key + "が必要です。");
            var allowed = command == "import" ? new[] { "--source", "--out", "--result" } : new[] { "--config", "--logs", "--result" };
            if (fields.Keys.Except(allowed).Any() || switches.Contains("--no-images") && command != "import" || switches.Contains("--now") && command != "run" && command != "watch") throw new ArgumentException("この操作では指定できない引数です。");
            string protectedFile = Path.GetFullPath(Required(command == "import" ? "--out" : "--config"));
            if (fields.TryGetValue("--result", out var requested))
            {
                string path = Path.GetFullPath(requested);
                if (path.Equals(protectedFile, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || new[] { "session.json", "edits.json" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("結果はconfigや記録とは別のJSONにしてください。");
                if (File.Exists(path))
                {
                    using var previous = JsonDocument.Parse(File.ReadAllText(path));
                    if (!previous.RootElement.TryGetProperty("product", out var product) || product.GetString() != "operation-recorder-replay") throw new ArgumentException("既存の結果JSON以外を上書きできません。");
                }
                resultPath = path;
            }
            if (command == "import")
            {
                var config = ReplayImporter.Import(Required("--source"));
                if (switches.Contains("--no-images")) foreach (var step in config.Steps) step.BeforeImage = step.AfterImage = null;
                config.Save(protectedFile);
                var errors = config.Validate(); result["config"] = protectedFile; result["ready"] = errors.Count == 0; result["errors"] = errors; result["steps"] = config.Steps.Count;
                result["status"] = errors.Count == 0 ? "ready" : "draft";
            }
            else
            {
                var config = ReplayConfig.Load(protectedFile);
                var errors = config.Validate(); if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
                if (config.Steps.Any(step => new[] { step.BeforeImage, step.AfterImage }.Any(g => g != null && string.Equals(config.PathOf(g.File), resultPath, StringComparison.OrdinalIgnoreCase)))) throw new ArgumentException("照合元画像を結果の保存先にはできません。");
                result["steps"] = config.Steps.Count;
                result["conditions"] = config.Steps.Select((s, i) => new { step = i + 1, s.Kind, s.Skip, element = s.Element != null, beforeImage = s.BeforeImage != null, afterImage = s.AfterImage != null, environmentInput = !string.IsNullOrEmpty(s.Text?.Environment) }).ToArray();
                if (command == "check") result["status"] = "ready";
                else
                {
                    using var execution = new Mutex(true, @"Local\OperationRecorder.Replay." + Environment.UserName, out bool first);
                    if (!first) throw new InvalidOperationException("別のリプレイが実行/監視中です。");
                    var existing = Process.GetProcessesByName("OperationRecorder");
                    try { if (existing.Any(p => p.Id != Environment.ProcessId)) throw new InvalidOperationException("記録との競合を避けるため、通常のOperationRecorderを終了してから再生してください。"); }
                    finally { foreach (var p in existing) p.Dispose(); }
                    WindowsReplayRuntime.CheckDesktop();
                    using var cancel = new CancellationTokenSource();
                    ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancel.Cancel(); };
                    Console.CancelKeyPress += onCancel;
                    try
                    {
                        using var hotkeys = new ReplayHotkeys(config.StopKey,
                            switches.Contains("--now") ? Array.Empty<string>() : config.Triggers.Where(t => t.Type == "hotkey").Select(t => t.Key), cancel);
                        if (hotkeys.Error != null) throw new InvalidOperationException(hotkeys.Error);
                        string baseLog = fields.TryGetValue("--logs", out var logs) ? Path.GetFullPath(logs) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OperationRecorder", "replay-logs");
                        logDirectory = Path.Combine(baseLog, "run-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(logDirectory);
                        using var stream = new StreamWriter(Path.Combine(logDirectory, "progress.jsonl")) { AutoFlush = true };
                        void Progress(ReplayProgress value)
                        {
                            stream.WriteLine(JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, value.Step, value.State, value.Message }));
                            Console.Error.WriteLine("[" + value.Step + "] " + value.Message);
                        }
                        var runtime = new WindowsReplayRuntime(config);
                        var monitor = new ReplayMonitor(runtime, id => runtime.ApplicationIdentity(config.Targets[id]), hotkeys.Consume, value => hotkeys.Enabled = value, runtime.EndRun, Progress);
                        try { monitor.Run(config, command == "watch", switches.Contains("--now"), cancel.Token).GetAwaiter().GetResult(); }
                        finally { result["runs"] = monitor.Runs; result["completedSteps"] = monitor.CompletedSteps; }
                        result["status"] = "completed";
                    }
                    finally { Console.CancelKeyPress -= onCancel; }
                }
            }
        }
        catch (Exception error)
        {
            code = error is OperationCanceledException ? 3 : error is ArgumentException || error is JsonException ? 2 : 1;
            result["status"] = code == 3 ? "cancelled" : "failed";
            result["errorType"] = error.GetType().Name;
            // UIA provider等の任意メッセージに入力内容が含まれないよう、既知の境界以外は要約する。
            result["message"] = error is ArgumentException || error is InvalidOperationException || error is TimeoutException ? error.Message : "リプレイ処理を完了できませんでした。";
        }
        result["exitCode"] = code; if (logDirectory != null) result["logDirectory"] = logDirectory;
        string json = JsonSerializer.Serialize(result, ReplayConfig.JsonOptions);
        try
        {
            if (logDirectory != null) File.WriteAllText(Path.Combine(logDirectory, "result.json"), json);
            if (resultPath != null) { Directory.CreateDirectory(Path.GetDirectoryName(resultPath)); File.WriteAllText(resultPath, json); }
        }
        catch (Exception) { code = 1; result["status"] = "failed"; result["exitCode"] = code; result["resultWriteError"] = "結果を書き出せませんでした。"; json = JsonSerializer.Serialize(result, ReplayConfig.JsonOptions); }
        Console.WriteLine(json); return code;
    }
}
