using System;
using System.Linq;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace OperationRecorderApp
{
    static class Program
    {
        // 起動方法
        //   OperationRecorder.exe            … アプリを開く(--settings と同じ)
        //   OperationRecorder.exe --settings … 設定の画面を出す。起動済みなら、起動中のアプリの設定の画面を出す
        //   OperationRecorder.exe --startup  … スタートアップからの起動用。画面を出さずタスクトレイに常駐する。起動済みなら何もしない
        //   OperationRecorder.exe --record   … 前回の設定ですぐ記録を始める(スタートメニューの「すぐに記録」)。起動済みなら、そのアプリで始める
        //   OperationRecorder.exe --export … 画面を出さずに、記録フォルダから md / HTML を出力する(ExportCommand.cs)
        [STAThread]
        static int Main(string[] args)
        {
            // 出力だけならホットキーもフックも使わないので、起動中のアプリがあっても動かしてよい
            if (ExportCommand.IsExport(args)) return ExportCommand.Execute(args);
            int settingsIndex = Array.IndexOf(args, "--settings-dir");
            if (settingsIndex >= 0)
            {
                if (settingsIndex + 1 >= args.Length || args[settingsIndex + 1].StartsWith("--")) return 2;
                AppSettings.SettingsDirectoryOverride = Path.GetFullPath(args[settingsIndex + 1]);
            }
            AppSettings.Passive = args.Contains("--passive");
            // ホットキーとキーボードフックを 2 つのプロセスで取り合わないよう、同時に 1 つだけ動かす
            using (var mutex = new Mutex(true, @"Local\OperationRecorderApp", out bool first))
            {
                var launch = LaunchOptions.Parse(args);
                if (!first)
                {
                    // 起動中のアプリに頼む(合図を受け取れない古い版が動いているときだけ、知らせて終わる)
                    bool shown = launch.ForRunningInstance() == LaunchOptions.RunningAction.ShowMain &&
                        KotorinToys.Integration.ManagementEndpoint.ShowExisting("operation-recorder");
                    if (!shown && !InstanceSignal.Send(launch.ForRunningInstance()))
                        MessageBox.Show("操作記録ツールはすでに起動しています。タスクトレイのアイコンから開いてください。",
                            "操作記録ツール", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                ApplicationConfiguration.Initialize();
                using (var window = new MainForm(launch.Record, launch.StartHidden))
                {
                    _ = window.Handle;
                    using (var management = new KotorinToys.Integration.ManagementEndpoint(
                        "operation-recorder", window, window.ShowManaged, window.StopManaged,
                        () => window.ManagementStopBlockReason))
                        Application.Run(window);
                }
            }
            return 0;
        }
    }
}
