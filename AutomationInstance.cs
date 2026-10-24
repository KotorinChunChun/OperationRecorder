using System;
using System.IO;
using System.Linq;

namespace OperationRecorderApp
{
    // 通常の常駐版とMutex・イベント・管理pipeを共有しない、明示指定の試験起動。
    public sealed class AutomationInstance
    {
        public string Token { get; private set; }
        public string MutexName => @"Local\OperationRecorderApp" + (Token == null ? "" : ".Test." + Token);
        public string SignalPrefix => Token == null ? InstanceSignal.DefaultPrefix : InstanceSignal.DefaultPrefix + "Test." + Token + ".";
        public string ManagementId => Token == null ? "operation-recorder" : "operation-recorder-test-" + Token;

        public static AutomationInstance Parse(string[] args)
        {
            var result = new AutomationInstance();
            int count = args.Count(value => value == "--test-instance");
            if (count == 0) return result;
            int index = Array.IndexOf(args, "--test-instance");
            if (count != 1 || index + 1 >= args.Length || !Guid.TryParseExact(args[index + 1], "N", out _))
                throw new ArgumentException("--test-instance には32桁のGUIDを1つ指定してください。");
            int settings = Array.IndexOf(args, "--settings-dir");
            if (!args.Contains("--passive") || settings < 0 || settings + 1 >= args.Length || !Path.IsPathFullyQualified(args[settings + 1]))
                throw new ArgumentException("試験インスタンスには --passive と絶対パスの --settings-dir が必要です。");
            AutomationWorkspace.CheckLinks(args[settings + 1]);
            var owner = Path.GetFullPath(args[settings + 1]);
            while (!string.IsNullOrEmpty(owner) && !File.Exists(Path.Combine(owner, AutomationWorkspace.MarkerName))) owner = Path.GetDirectoryName(owner);
            if (string.IsNullOrEmpty(owner)) throw new ArgumentException("試験インスタンスの設定は所有マーカー付きworkspace内へ保存してください。");
            var workspace = new AutomationWorkspace(owner);
            var settingsPath = workspace.Resolve(args[settings + 1]);
            if (File.Exists(settingsPath)) throw new ArgumentException("試験設定にはフォルダーを指定してください。");
            result.Token = Guid.ParseExact(args[index + 1], "N").ToString("N");
            return result;
        }
    }
}
