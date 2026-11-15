using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using OpRec;

namespace OperationRecorderApp
{
    static class NarrationTranscription
    {
        public static int? Save(TranscriptionSettings options, string directory, long audioStartMs)
        {
            if (options.Provider == TranscriptionProvider.None) return null;
            string audio = Path.Combine(directory, "narration.wav");
            var results = options.Provider is TranscriptionProvider.Whisper or TranscriptionProvider.Haas
                ? RunLocal(options, audio)
                : TranscriptionApi.Run(options, new TranscriptionCredentials(AppSettings.SettingsDir), audio).GetAwaiter().GetResult();
            return SessionTranscript.Save(directory, results, audioStartMs);
        }

        static JsonArray RunLocal(TranscriptionSettings options, string audio)
        {
            if (string.IsNullOrEmpty(options.Python) || !Path.IsPathFullyQualified(options.Python) || !File.Exists(options.Python))
                throw new InvalidOperationException("「撮影の設定」で文字起こし用のPythonを指定してください。");
            string[] requiredFiles = options.Provider == TranscriptionProvider.Haas
                ? ["encoder.onnx", "decoder.onnx", "joiner.onnx", "tokens.txt"] : ["model.bin"];
            if (string.IsNullOrEmpty(options.LocalModel) || !Path.IsPathFullyQualified(options.LocalModel) ||
                Array.Exists(requiredFiles, file => !File.Exists(Path.Combine(options.LocalModel, file))))
                throw new InvalidOperationException("選択した文字起こし方式に対応するモデルフォルダーを指定してください。");

            string result = audio + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var launch = new ProcessStartInfo(options.Python) { UseShellExecute = false, CreateNoWindow = true };
                launch.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "scripts", "transcribe-narration.py"));
                launch.ArgumentList.Add(audio);
                launch.ArgumentList.Add(options.LocalModel);
                launch.ArgumentList.Add(result);
                launch.ArgumentList.Add(options.Provider.ToString());
                using var process = Process.Start(launch) ?? throw new IOException("文字起こしを開始できません。");
                if (!process.WaitForExit(30 * 60 * 1000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                    throw new TimeoutException("文字起こしが30分以内に完了しませんでした。");
                }
                if (process.ExitCode != 0 || !File.Exists(result) || new FileInfo(result).Length > SessionTranscript.MaximumBytes)
                    throw new IOException("ローカル文字起こしに失敗しました。選択したPython環境とモデルを確認してください。");
                return JsonNode.Parse(File.ReadAllText(result)).AsArray();
            }
            finally { File.Delete(result); }
        }
    }
}
