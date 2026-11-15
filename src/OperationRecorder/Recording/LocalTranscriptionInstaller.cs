using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace OperationRecorderApp
{
    internal sealed class LocalTranscriptionInstaller(string settingsDirectory)
    {
        const string Marker = ".operation-recorder-model";
        const string Ready = ".ready";
        const string UvRelease = "https://github.com/astral-sh/uv/releases/download/0.13.7/uv-x86_64-pc-windows-msvc.zip";
        const string UvHash = "0cdc5b38fe5a8168ed6a25814f5ba43c21d313dd3187314fccb37b7cec46e021";

        static readonly (string File, string Hash)[] WhisperFiles =
        [
            ("config.json", "b55496ac7940a7ae47d2c01eab40edfd8701feec1229d9cce3b40014383fb828"),
            ("model.bin", "3e305921506d8872816023e4c273e75d2419fb89b24da97b4fe7bce14170d671"),
            ("tokenizer.json", "fb7b63191e9bb045082c79fd742a3106a12c99513ab30df4a0d47fa6cb6fd0ab"),
            ("vocabulary.txt", "34ce3fe1c5041027b3f8d42912270993f986dbc4bb34cf27f951e34a1e453913"),
        ];

        static readonly (string File, string Hash)[] HaasFiles =
        [
            ("encoder.data", "7aa9d630b66a2317f615c7094c201ca1049d3b14b0a2515f42a8eea0c17e410a"),
            ("encoder.onnx", "8e34e59e7a2dff1dbda2d91c4ba1d9673f976da74db25597883a185088300757"),
            ("decoder.onnx", "2947cb5512699cbba753fe45fe221428aadaa9856597debbb7aabe19cc253622"),
            ("joiner.onnx", "8a68320e750890ff9e944a794b41ae1c42c6f439ac4c273503458a6b42adc12d"),
            ("tokens.txt", "ec78b13c5d9f156cf0d5fdbd6b5aec5785680f319c92630c11d5d11b14b83a84"),
            ("LICENSE", "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30"),
            ("NOTICE", "238ea54072bae4fb3e2da605a71ddf0b879a7e5575365b00b4761b3edd205204"),
        ];

        string Root(TranscriptionProvider provider)
        {
            if (provider is not (TranscriptionProvider.Haas or TranscriptionProvider.Whisper))
                throw new ArgumentException("WhisperかHaasを選んでください。");
            return Path.GetFullPath(Path.Combine(settingsDirectory, "local-models", provider.ToString()));
        }

        public (string Python, string Model) Paths(TranscriptionProvider provider)
        {
            string root = Root(provider);
            return (Path.Combine(root, "venv", "Scripts", "python.exe"), Path.Combine(root, "model"));
        }

        public bool Installed(TranscriptionProvider provider)
        {
            string root = Root(provider);
            var (python, model) = Paths(provider);
            return File.Exists(Path.Combine(root, Marker)) && File.Exists(Path.Combine(root, Ready)) &&
                File.Exists(python) && (provider == TranscriptionProvider.Haas ? HaasFiles : WhisperFiles)
                .All(file => File.Exists(Path.Combine(model, file.File)));
        }

        public void Remove(TranscriptionProvider provider)
        {
            string root = Root(provider);
            AutomationWorkspace.CheckLinks(root);
            using var lease = Acquire(root);
            RemoveOwnedDirectory(provider, root);
        }

        static FileStream Acquire(string root)
        {
            string path = root + ".lock";
            AutomationWorkspace.CheckLinks(path);
            Directory.CreateDirectory(Path.GetDirectoryName(root));
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        static void RemoveOwnedDirectory(TranscriptionProvider provider, string root)
        {
            if (!Directory.Exists(root)) return;
            string marker = Path.Combine(root, Marker);
            AutomationWorkspace.CheckLinks(marker);
            if (!File.Exists(marker) || !string.Equals(File.ReadAllText(marker), provider.ToString(), StringComparison.Ordinal))
                throw new IOException("このツールが導入したフォルダーと確認できないため削除できません。");
            foreach (string entry in Directory.EnumerateFileSystemEntries(root))
                if (!string.Equals(entry, marker, StringComparison.OrdinalIgnoreCase)) RemoveEntry(entry);
            File.Delete(marker);
            try { Directory.Delete(root); }
            catch (IOException) { if (Directory.Exists(root)) File.WriteAllText(marker, provider.ToString()); throw; }
            catch (UnauthorizedAccessException) { if (Directory.Exists(root)) File.WriteAllText(marker, provider.ToString()); throw; }
        }

        static void RemoveEntry(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return;
            var attributes = File.GetAttributes(path);
            bool directory = (attributes & FileAttributes.Directory) != 0;
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                if (directory)
                    foreach (string entry in Directory.EnumerateFileSystemEntries(path)) RemoveEntry(entry);
            }
            if (directory) Directory.Delete(path);
            else File.Delete(path);
        }

        public async Task Install(TranscriptionProvider provider, IProgress<string> progress, CancellationToken cancellation)
        {
            string root = Root(provider);
            AutomationWorkspace.CheckLinks(root);
            using var lease = Acquire(root);
            if (Installed(provider)) return;
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("自動導入はx64版に対応しています。手動でPythonとモデルを参照してください。");
            if (Directory.Exists(root)) RemoveOwnedDirectory(provider, root);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, Marker), provider.ToString());
            var (python, model) = Paths(provider);
            bool haas = provider == TranscriptionProvider.Haas;
            string package = haas ? "sherpa-onnx==1.13.6" : "faster-whisper==1.2.1";
            string repository = haas ? "oboroge0/Haas-ja-v1.0-onnx/resolve/2c2133a4279fca8ee408dd33b5701690a0bd4f3f"
                : "Systran/faster-whisper-small/resolve/536b0662742c02347bc0e980a01041f333bce120";
            try
            {
                using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                progress?.Report("Python環境を準備中…");
                string archive = Path.Combine(root, "uv.zip"), uv = Path.Combine(root, "uv.exe");
                await Download(client, UvRelease, UvHash, archive, cancellation).ConfigureAwait(false);
                using (var zip = ZipFile.OpenRead(archive))
                {
                    var entry = zip.Entries.Single(file => file.Name == "uv.exe");
                    entry.ExtractToFile(uv);
                }
                File.Delete(archive);
                await Execute(uv, root, cancellation, "python", "install", "3.12", "--no-bin").ConfigureAwait(false);
                await Execute(uv, root, cancellation, "venv", "--managed-python", "--python", "3.12", Path.Combine(root, "venv")).ConfigureAwait(false);
                await Execute(uv, root, cancellation, "pip", "install", "--python", python, "--only-binary", ":all:",
                    "--default-index", "https://pypi.org/simple", package, "numpy").ConfigureAwait(false);
                await Execute(python, root, cancellation, "-c", haas ? "import sherpa_onnx, numpy" : "import faster_whisper").ConfigureAwait(false);
                Directory.CreateDirectory(model);
                foreach (var (file, hash) in haas ? HaasFiles : WhisperFiles)
                {
                    progress?.Report("モデルを取得中（" + file + "）…");
                    await Download(client, "https://huggingface.co/" + repository + "/" + file, hash,
                        Path.Combine(model, file), cancellation).ConfigureAwait(false);
                }
                File.WriteAllText(Path.Combine(root, Ready), "");
            }
            catch
            {
                try { RemoveOwnedDirectory(provider, root); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                throw;
            }
        }

        static async Task Download(HttpClient client, string url, string hash, string local, CancellationToken token)
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var file = new FileStream(local, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true);
            await response.Content.CopyToAsync(file, token).ConfigureAwait(false);
            file.Position = 0;
            if (!string.Equals(hash, Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ダウンロードしたファイルの照合に失敗しました。");
        }

        static async Task Execute(string executable, string root, CancellationToken token, params string[] arguments)
        {
            var launch = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
            foreach (string key in launch.Environment.Keys.Where(key => key.StartsWith("UV_", StringComparison.OrdinalIgnoreCase)).ToArray())
                launch.Environment.Remove(key);
            launch.Environment["UV_NO_CONFIG"] = "1";
            launch.Environment["UV_NO_CACHE"] = "1";
            launch.Environment["UV_PYTHON_INSTALL_DIR"] = Path.Combine(root, "python");
            launch.Environment["UV_PYTHON_BIN_DIR"] = Path.Combine(root, "bin");
            foreach (string argument in arguments) launch.ArgumentList.Add(argument);
            using var process = Process.Start(launch) ?? throw new IOException("Pythonのセットアップを開始できません。");
            try { await process.WaitForExitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
                throw;
            }
            if (process.ExitCode != 0) throw new IOException("Pythonのセットアップに失敗しました。ネットワーク接続を確認してください。");
        }
    }
}
