using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace OperationRecorderApp
{
    // 利用者との確認とOSへの副作用をまとめる。CLI試験はこれを差し替え、通常の環境へ触れない。
    public class AppInteraction
    {
        public virtual DialogResult Message(IWin32Window owner, string text, string title, MessageBoxButtons buttons, MessageBoxIcon icon) =>
            MessageBox.Show(owner, text, title, buttons, icon);

        public virtual string SelectFolder(IWin32Window owner, string description, string selectedPath)
        {
            using (var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true, ShowNewFolderButton = true })
            {
                if (Directory.Exists(selectedPath)) dialog.SelectedPath = selectedPath;
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        public virtual string OpenFile(IWin32Window owner, string title, string filter, string initialDirectory)
        {
            using (var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true })
            {
                if (Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
            }
        }

        public virtual void OpenPath(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        public virtual void RecycleFile(string path) => RecycleBin.SendFile(path);
        public virtual void RecycleDirectory(string path) => RecycleBin.SendDirectory(path);

        public virtual bool ShortcutIsInstalled => ShortcutInstaller.IsInstalled;
        public virtual bool ShortcutPointsToThisExe => ShortcutInstaller.PointsToThisExe();
        public virtual bool InstallerShortcutPointsToThisExe => ShortcutInstaller.InstallerShortcutPointsToThisExe();

        static void EnsureNormalIntegration()
        {
            if (AppSettings.Passive || AppSettings.SettingsDirectoryOverride != null)
                throw new InvalidOperationException("隔離した実行ではショートカット変更と管理者再起動を行いません。");
        }

        public virtual void InstallShortcuts() { EnsureNormalIntegration(); ShortcutInstaller.Install(); }
        public virtual void UninstallShortcuts() { EnsureNormalIntegration(); ShortcutInstaller.Uninstall(); }
        public virtual void RestartAsAdmin(string executable)
        {
            EnsureNormalIntegration();
            // 起動中のMutexを解放する猶予を置き、UAC取消時は呼出元が終了しないよう例外を返す。
            Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 2 >nul & start \"\" \"" + executable + "\"")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
        }
    }
}
