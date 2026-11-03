using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace OperationRecorderApp
{
    // 起動の引数と、すでに起動しているときにどうするか。画面に頼らないので自動テストから確かめられる
    //   --settings … 起動して設定の画面を出す。起動済みなら、起動中のアプリの設定の画面を出す(引数なしと同じ)
    //   --startup  … スタートアップからの起動で使う。画面を出さず、タスクトレイに常駐して起動する。起動済みなら何もしない
    //   --record   … 前回の設定ですぐ記録を始める。起動済みなら、起動中のアプリで記録を始める
    public sealed class LaunchOptions
    {
        public bool Settings;  // 設定の画面を出す
        public bool Startup;   // 画面を出さず、タスクトレイに常駐して起動する
        public bool Record;    // 前回の設定ですぐ記録を始める(スタートメニューの「すぐに記録」)

        // 「--startup」「-startup」「/startup」「startup」のどれでもよい(大文字・小文字は問わない)
        public static LaunchOptions Parse(string[] args)
        {
            var names = (args ?? new string[0]).Select(a => (a ?? "").Trim().TrimStart('-', '/').ToLowerInvariant()).ToList();
            return new LaunchOptions { Settings = names.Contains("settings"), Startup = names.Contains("startup"), Record = names.Contains("record") };
        }

        // 起動したとき、画面を出さずに始めるか(--startup だけのとき)
        public bool StartHidden => Startup && !Settings && !Record;

        public enum RunningAction { None, ShowMain, StartRecording }

        // すでに起動しているとき、起動中のアプリに頼むこと
        public RunningAction ForRunningInstance() =>
            Record ? RunningAction.StartRecording : Settings ? RunningAction.ShowMain : Startup ? RunningAction.None : RunningAction.ShowMain;
    }

    // 起動中のアプリに「画面を出して」「記録を始めて」と伝える(名前付きのイベント。同じユーザーのセッションの中だけ)
    public sealed class InstanceSignal : IDisposable
    {
        public const string DefaultPrefix = @"Local\OperationRecorderApp.";
        readonly EventWaitHandle show, record;
        readonly RegisteredWaitHandle showWait, recordWait;

        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

        // 起動したアプリが待ち受ける。合図が来たら、待ち受けを作ったスレッドとは別のスレッドで呼ぶ
        public InstanceSignal(Action onShow, Action onRecord, string prefix = DefaultPrefix)
        {
            show = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + "Show");
            record = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + "Record");
            showWait = ThreadPool.RegisterWaitForSingleObject(show, (s, t) => onShow?.Invoke(), null, Timeout.Infinite, false);
            recordWait = ThreadPool.RegisterWaitForSingleObject(record, (s, t) => onRecord?.Invoke(), null, Timeout.Infinite, false);
        }

        // 2 つ目に起動したアプリから合図を送る。待ち受けが無ければ false
        public static bool Send(LaunchOptions.RunningAction action, string prefix = DefaultPrefix)
        {
            if (action == LaunchOptions.RunningAction.None) return true;
            string name = prefix + (action == LaunchOptions.RunningAction.StartRecording ? "Record" : "Show");
            try
            {
                using (var e = EventWaitHandle.OpenExisting(name))
                {
                    // 起動中のアプリが画面を前に出せるように、前面に出る権利を渡しておく(利用者が起動したこのプロセスが持っている)
                    AllowSetForegroundWindow(-1 /*ASFW_ANY*/);
                    return e.Set();
                }
            }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        public void Dispose()
        {
            showWait.Unregister(null);
            recordWait.Unregister(null);
            show.Dispose();
            record.Dispose();
        }
    }

    // Windows にサインインしたときに起動する(このユーザーの「スタートアップ」フォルダに、--startup 付きのショートカットを置く)
    public static class StartupRegistration
    {
        public const string LinkName = "操作記録ツール.lnk";
        public const string Arguments = "--startup";

        public static string DefaultFolder => Environment.GetFolderPath(Environment.SpecialFolder.Startup);

        static string LinkPath(string folder) => Path.Combine(folder ?? DefaultFolder, LinkName);

        public static bool IsEnabled(string folder = null) => File.Exists(LinkPath(folder));

        // 登録済みのショートカットが、この exe を今の引数で起動するか(exe を別の場所へ移した・以前の引数のままなら false)
        public static bool PointsTo(string exePath, string folder = null) =>
            IsEnabled(folder) && string.Equals(ShortcutInstaller.TargetOf(LinkPath(folder)), exePath, StringComparison.OrdinalIgnoreCase)
            && ArgumentsOf(folder) == Arguments;

        public static string ArgumentsOf(string folder = null) => IsEnabled(folder) ? ShortcutInstaller.ArgumentsOf(LinkPath(folder)) : null;
    }
}
