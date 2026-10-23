using System;
using System.Reflection;

namespace OperationRecorderApp
{
    // 製品版を実行アセンブリから読み、独自画面のタイトルを一か所で組み立てる。
    public static class WindowTitle
    {
        public static string Create(string screen = null)
        {
            var assembly = typeof(WindowTitle).Assembly;
            var productVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
            if (!Version.TryParse(productVersion, out _))
            {
                var version = assembly.GetName().Version;
                productVersion = version.Revision > 0 ? version.ToString(4) : version.ToString(3);
            }
            var title = "OperationRecorder - ver " + productVersion;
            return string.IsNullOrWhiteSpace(screen) ? title : screen + " - " + title;
        }
    }
}
