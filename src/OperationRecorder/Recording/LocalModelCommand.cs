using System;
using System.IO;
using System.Threading;

namespace OperationRecorderApp
{
    internal static class LocalModelCommand
    {
        public static int Execute(string[] args)
        {
            CommandConsole.Prepare();
            if (args.Length is not (3 or 5) || args[1] is not ("install" or "remove" or "status") ||
                !Enum.TryParse(args[2], ignoreCase: true, out TranscriptionProvider provider) ||
                provider is not (TranscriptionProvider.Whisper or TranscriptionProvider.Haas) ||
                (args.Length == 5 && (args[3] != "--settings-dir" || args[4].StartsWith("--", StringComparison.Ordinal))))
            {
                Console.Error.WriteLine("使用方法: OperationRecorder.exe --local-transcription-model install|remove|status Whisper|Haas [--settings-dir <設定フォルダー>]");
                return 2;
            }
            try
            {
                string directory = args.Length == 5 ? Path.GetFullPath(args[4]) : AppSettings.SettingsDir;
                var installer = new LocalTranscriptionInstaller(directory);
                if (args[1] == "install")
                {
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromHours(1));
                    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
                    Console.CancelKeyPress += cancel;
                    try { installer.Install(provider, new ConsoleProgress(), cancellation.Token).GetAwaiter().GetResult(); }
                    finally { Console.CancelKeyPress -= cancel; }
                }
                if (args[1] == "remove") installer.Remove(provider);
                if (installer.Installed(provider))
                {
                    var paths = installer.Paths(provider);
                    Console.WriteLine($"導入済み {provider}\nPython: {paths.Python}\nモデル: {paths.Model}");
                }
                else Console.WriteLine($"未導入 {provider}");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("ローカルモデルを処理できません。 " + error.Message);
                return 1;
            }
        }

        sealed class ConsoleProgress : IProgress<string>
        {
            public void Report(string value) => Console.Error.WriteLine(value);
        }
    }
}
