using System;
using System.IO;
using Xunit;

namespace OperationRecorderApp.Tests
{
    [Collection("管理画面の隔離設定")]
    public sealed class AppInteractionTests
    {
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void 隔離指定では実OSへの登録解除と管理者再起動を拒否する(bool passive, bool settingsOverride)
        {
            bool oldPassive = AppSettings.Passive;
            string oldDirectory = AppSettings.SettingsDirectoryOverride;
            try
            {
                AppSettings.Passive = passive;
                AppSettings.SettingsDirectoryOverride = settingsOverride ? Path.GetTempPath() : null;
                var interaction = new AppInteraction();
                Assert.Contains("隔離", Assert.Throws<InvalidOperationException>(() => interaction.InstallShortcuts()).Message);
                Assert.Contains("隔離", Assert.Throws<InvalidOperationException>(() => interaction.UninstallShortcuts()).Message);
                Assert.Contains("隔離", Assert.Throws<InvalidOperationException>(() => interaction.RestartAsAdmin("never-execute.exe")).Message);
            }
            finally { AppSettings.Passive = oldPassive; AppSettings.SettingsDirectoryOverride = oldDirectory; }
        }
    }
}
