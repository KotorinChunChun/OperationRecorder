using System;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Speech.Recognition;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using OpRec;

namespace OperationRecorderApp
{
    static class NarrationTranscription
    {
        public static int? Save(TranscriptionSettings options, string directory, long audioStartMs)
        {
            if (options.Provider == TranscriptionProvider.None) return null;
            var results = Recognize(options, Path.Combine(directory, "narration.wav"), CancellationToken.None);
            return SessionTranscript.Save(directory, results, audioStartMs);
        }

        internal static JsonArray Recognize(TranscriptionSettings options, string audio, CancellationToken cancellation) =>
            options.Provider switch
            {
                TranscriptionProvider.Whisper or TranscriptionProvider.Haas => RunLocal(options, audio, cancellation),
                TranscriptionProvider.Windows => RunWindows(audio, cancellation),
                _ => TranscriptionApi.Run(options, new TranscriptionCredentials(AppSettings.SettingsDir), audio, cancellation)
                    .GetAwaiter().GetResult(),
            };

        static JsonArray RunWindows(string audio, CancellationToken cancellation)
        {
            var installed = SpeechRecognitionEngine.InstalledRecognizers();
            var recognizer = installed.FirstOrDefault(r => r.Culture.Name == CultureInfo.CurrentUICulture.Name)
                ?? installed.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)
                ?? installed.FirstOrDefault();
            if (recognizer == null) throw new InvalidOperationException("Windowsに対応する音声認識モデルがありません。");

            using var engine = new SpeechRecognitionEngine(recognizer);
            engine.LoadGrammar(new DictationGrammar());
            engine.SetInputToWaveFile(audio);
            var result = new JsonArray();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.SpeechRecognized += (_, e) =>
            {
                var clip = e.Result.Audio;
                if (clip == null || clip.Duration <= TimeSpan.Zero || string.IsNullOrWhiteSpace(e.Result.Text)) return;
                result.Add(new JsonObject
                {
                    ["startMs"] = (long)clip.AudioPosition.TotalMilliseconds,
                    ["endMs"] = (long)(clip.AudioPosition + clip.Duration).TotalMilliseconds,
                    ["text"] = e.Result.Text,
                });
            };
            engine.RecognizeCompleted += (_, e) =>
            {
                if (e.Error != null) done.TrySetException(e.Error);
                else done.TrySetResult();
            };
            cancellation.ThrowIfCancellationRequested();
            engine.RecognizeAsync(RecognizeMode.Multiple);
            try { done.Task.WaitAsync(TimeSpan.FromMinutes(30), cancellation).GetAwaiter().GetResult(); }
            catch { engine.RecognizeAsyncCancel(); throw; }
            return result;
        }

        static JsonArray RunLocal(TranscriptionSettings options, string audio, CancellationToken cancellation)
        {
            if (string.IsNullOrEmpty(options.Python) || !Path.IsPathFullyQualified(options.Python) || !File.Exists(options.Python))
                throw new InvalidOperationException("「記録対象」で文字起こし用のPythonを指定してください。");
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
                try
                {
                    process.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromMinutes(30), cancellation).GetAwaiter().GetResult();
                }
                catch (Exception error) when (error is TimeoutException or OperationCanceledException)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                    throw;
                }
                if (process.ExitCode != 0 || !File.Exists(result) || new FileInfo(result).Length > SessionTranscript.MaximumBytes)
                    throw new IOException("ローカル文字起こしに失敗しました。選択したPython環境とモデルを確認してください。");
                return JsonNode.Parse(File.ReadAllText(result)).AsArray();
            }
            finally { File.Delete(result); }
        }
    }
}
