using System;
using System.IO;
using System.Threading;
using Xunit;

namespace OperationRecorderApp.Tests
{
    // 起動の引数・起動済みのときの合図・Windows の起動時の登録
    public class LaunchTests
    {
        [Theory]
        [InlineData(new string[0], false, false, false)]
        [InlineData(new[] { "--settings" }, true, false, false)]
        [InlineData(new[] { "--startup" }, false, true, false)]
        [InlineData(new[] { "-startup" }, false, true, false)]
        [InlineData(new[] { "/STARTUP" }, false, true, false)]
        [InlineData(new[] { "startup" }, false, true, false)]
        [InlineData(new[] { "--record" }, false, false, true)]
        [InlineData(new[] { "--hide" }, false, false, false)]   // --hide はやめた(何も指定しないのと同じ)
        public void 引数を読む(string[] args, bool settings, bool startup, bool record)
        {
            var o = LaunchOptions.Parse(args);
            Assert.Equal(settings, o.Settings);
            Assert.Equal(startup, o.Startup);
            Assert.Equal(record, o.Record);
        }

        [Fact]
        public void startupだけが画面を出さずに起動する()
        {
            Assert.True(LaunchOptions.Parse(new[] { "--startup" }).StartHidden);
            Assert.False(LaunchOptions.Parse(new string[0]).StartHidden);
            Assert.False(LaunchOptions.Parse(new[] { "--settings" }).StartHidden);
            Assert.False(LaunchOptions.Parse(new[] { "--startup", "--settings" }).StartHidden);   // 設定を出すと言われたら出す
            Assert.False(LaunchOptions.Parse(new[] { "--hide" }).StartHidden);
        }

        [Theory]
        [InlineData(new string[0], LaunchOptions.RunningAction.ShowMain)]
        [InlineData(new[] { "--settings" }, LaunchOptions.RunningAction.ShowMain)]      // 起動済みなら設定を出す
        [InlineData(new[] { "--startup" }, LaunchOptions.RunningAction.None)]           // 起動済みなら何もしない
        [InlineData(new[] { "--record" }, LaunchOptions.RunningAction.StartRecording)]  // 起動中のアプリで記録を始める
        [InlineData(new[] { "--startup", "--settings" }, LaunchOptions.RunningAction.ShowMain)]
        public void 起動済みのときの動き(string[] args, LaunchOptions.RunningAction expected) =>
            Assert.Equal(expected, LaunchOptions.Parse(args).ForRunningInstance());

        // テストごとに別の名前のイベントを使う(起動中の本物のアプリには届かない)
        static string UniquePrefix() => @"Local\OperationRecorderTest." + Guid.NewGuid().ToString("N") + ".";

        [Fact]
        public void 起動中のアプリに合図が届く()
        {
            string prefix = UniquePrefix();
            using (var shown = new ManualResetEventSlim())
            using (var recorded = new ManualResetEventSlim())
            using (new InstanceSignal(() => shown.Set(), () => recorded.Set(), prefix))
            {
                Assert.True(InstanceSignal.Send(LaunchOptions.RunningAction.ShowMain, prefix));
                Assert.True(shown.Wait(5000), "画面を出す合図が届かない");
                Assert.False(recorded.IsSet);
                Assert.True(InstanceSignal.Send(LaunchOptions.RunningAction.StartRecording, prefix));
                Assert.True(recorded.Wait(5000), "記録を始める合図が届かない");
            }
        }

        [Fact]
        public void 何もしないときは合図を送らない()
        {
            string prefix = UniquePrefix();
            using (var shown = new ManualResetEventSlim())
            using (new InstanceSignal(() => shown.Set(), null, prefix))
            {
                Assert.True(InstanceSignal.Send(LaunchOptions.RunningAction.None, prefix));
                Assert.False(shown.Wait(300));
            }
        }

        [Fact]
        public void 待ち受けが無ければ送れない() =>
            Assert.False(InstanceSignal.Send(LaunchOptions.RunningAction.ShowMain, UniquePrefix()));

        [Fact]
        public void 正式版のショートカットは現在の実行パスと一致するときだけ初回提案を抑止する()
        {
            string folder = Path.Combine(Path.GetTempPath(), "oprec-menu-" + Guid.NewGuid().ToString("N"));
            string exe = typeof(LaunchOptions).Assembly.Location;
            string link = Path.Combine(folder, "OperationRecorder.lnk");
            try
            {
                Assert.False(ShortcutInstaller.InstallerShortcutPointsToThisExe(folder, exe));
                Assert.False(Directory.Exists(folder));
                Directory.CreateDirectory(folder);
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                try
                {
                    dynamic shortcut = shell.CreateShortcut(link);
                    shortcut.TargetPath = exe;
                    shortcut.Save();
                    byte[] before = File.ReadAllBytes(link);
                    Assert.True(ShortcutInstaller.InstallerShortcutPointsToThisExe(folder, exe));
                    Assert.False(ShortcutInstaller.InstallerShortcutPointsToThisExe(folder, Path.Combine(folder, "other.exe")));
                    Assert.Equal(before, File.ReadAllBytes(link));
                }
                finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }

        [Fact]
        public void 旧スタートアップ登録を読み取っても書き換えない()
        {
            string folder = Path.Combine(Path.GetTempPath(), "oprec-startup-" + Guid.NewGuid().ToString("N"));
            string exe = typeof(LaunchOptions).Assembly.Location;   // 実在するファイルなら何でもよい
            try
            {
                Assert.False(StartupRegistration.IsEnabled(folder));
                Directory.CreateDirectory(folder);
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                dynamic sc = shell.CreateShortcut(Path.Combine(folder, StartupRegistration.LinkName));
                sc.TargetPath = exe;
                sc.Arguments = "--startup";
                sc.Save();
                Assert.True(StartupRegistration.IsEnabled(folder));
                Assert.True(File.Exists(Path.Combine(folder, StartupRegistration.LinkName)));
                Assert.Equal("--startup", StartupRegistration.ArgumentsOf(folder));   // 起動時は画面を出さない
                Assert.True(StartupRegistration.PointsTo(exe, folder));
                Assert.False(StartupRegistration.PointsTo(Path.Combine(folder, "other.exe"), folder));
                // 以前の引数(--hide)のままのショートカットは、今の exe でも「直す」必要がある
                sc.Arguments = "--hide";
                sc.Save();
                Assert.False(StartupRegistration.PointsTo(exe, folder));
                Assert.Equal("--hide", StartupRegistration.ArgumentsOf(folder));
                Assert.True(StartupRegistration.IsEnabled(folder));
            }
            finally { try { Directory.Delete(folder, true); } catch { } }
        }
    }
}
