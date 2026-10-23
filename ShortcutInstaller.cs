using System;
using System.IO;

namespace OperationRecorderApp
{
    // スタートメニュー(このユーザーだけ)にショートカットを作る・消す。管理者権限は要らない
    //   操作記録ツール.lnk              … アプリを開く
    //   操作記録ツール(すぐに記録).lnk … 前回の設定ですぐ記録を始める(--record)
    public static class ShortcutInstaller
    {
        public const string FolderName = "操作記録ツール";

        public static string MenuFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), FolderName);

        static string AppLink => Path.Combine(MenuFolder, "操作記録ツール.lnk");
        static string RecordLink => Path.Combine(MenuFolder, "操作記録ツール(すぐに記録).lnk");

        public static bool IsInstalled => File.Exists(AppLink);

        // 正式インストーラーが作成したショートカットがあれば、初回の重複登録提案を出さない。
        // 古い単体版の日本語フォルダー内の登録は、ここでは変更しない。
        public static bool InstallerShortcutPointsToThisExe(string programsFolder = null, string exePath = null)
        {
            string link = Path.Combine(programsFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs), "OperationRecorder.lnk");
            try
            {
                return File.Exists(link) && string.Equals(TargetOf(link), exePath ?? Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // 登録済みのショートカットが、今動いている exe を指しているか(exe を別の場所へ移した後の確認用)
        public static bool PointsToThisExe()
        {
            try { return IsInstalled && string.Equals(TargetOf(AppLink), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        public static void Install()
        {
            Directory.CreateDirectory(MenuFolder);
            string exe = Environment.ProcessPath;
            Create(AppLink, exe, "", "操作を記録して、スクリーンショット付きの操作手順書(md)を作ります");
            Create(RecordLink, exe, "--record", "前回の設定で、すぐに操作の記録を始めます");
        }

        public static void Uninstall()
        {
            if (Directory.Exists(MenuFolder)) Directory.Delete(MenuFolder, true);
        }

        internal static void Create(string lnk, string target, string args, string description)
        {
            // WScript.Shell の COM を遅延バインディングで使う(参照の追加が要らない)
            var type = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(type);
            try
            {
                dynamic sc = shell.CreateShortcut(lnk);
                sc.TargetPath = target;
                sc.Arguments = args;
                sc.WorkingDirectory = Path.GetDirectoryName(target);
                sc.Description = description;
                sc.IconLocation = target + ",0";
                sc.Save();
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }

        internal static string TargetOf(string lnk)
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(type);
            try { return (string)shell.CreateShortcut(lnk).TargetPath; }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }

        internal static string ArgumentsOf(string lnk)
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(type);
            try { return (string)shell.CreateShortcut(lnk).Arguments; }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
    }
}
