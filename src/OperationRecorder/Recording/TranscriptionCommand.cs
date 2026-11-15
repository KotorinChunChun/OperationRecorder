using System;
using System.IO;

namespace OperationRecorderApp
{
    static class TranscriptionCommand
    {
        public static bool IsTranscription(string[] args) => args.Length != 0 && args[0] == "--transcribe";

        public static int Execute(string[] args)
        {
            CommandConsole.Prepare();
            if (args.Length is not (2 or 4) || args[1].StartsWith("--", StringComparison.Ordinal) ||
                (args.Length == 4 && (args[2] != "--settings-dir" || args[3].StartsWith("--", StringComparison.Ordinal))))
            {
                Console.Error.WriteLine("使用方法: OperationRecorder.exe --transcribe <記録フォルダー> [--settings-dir <設定フォルダー>]");
                return 2;
            }

            try
            {
                if (args.Length == 4) AppSettings.SettingsDirectoryOverride = Path.GetFullPath(args[3]);
                var settings = AppSettings.Load();
                if (TranscriptionSettings.SelectedProvider(settings) == TranscriptionProvider.None)
                {
                    Console.Error.WriteLine("文字起こしが無効です。「記録対象」で文字起こし方式を選択してください。");
                    return 2;
                }
                string directory = Path.GetFullPath(args[1]);
                var audio = RecordedNarration.Read(directory);
                int? count = NarrationTranscription.Save(TranscriptionSettings.From(settings), directory, audio.StartMs);
                Console.WriteLine($"文字起こしを保存しました（{count}区間）。");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("文字起こしに失敗しました: " + error.Message);
                return 1;
            }
        }
    }
}
