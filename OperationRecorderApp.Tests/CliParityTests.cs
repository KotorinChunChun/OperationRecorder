using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace OperationRecorderApp.Tests
{
    // スキル(pwsh)の New-OperationReport.ps1 と、アプリの出力が同じ md になるか。
    // pwsh やスキルのフォルダが見つからない環境では何もしない(合格扱い。理由を出力に書く)
    public class CliParityTests : IDisposable
    {
        readonly TestSession s = new TestSession();
        readonly ITestOutputHelper log;
        public CliParityTests(ITestOutputHelper log) { this.log = log; }
        public void Dispose() => s.Dispose();

        static string FindSkillScripts()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                foreach (var relative in new[] { "operation-recorder", Path.Combine("skills", "operation-recorder") })
                {
                    string p = Path.Combine(d.FullName, relative, "scripts");
                    if (File.Exists(Path.Combine(p, "New-OperationReport.ps1"))) return p;
                }
            }
            return null;
        }

        static string FindPwsh()
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try { string p = Path.Combine(dir.Trim(), "pwsh.exe"); if (File.Exists(p)) return p; } catch { }
            }
            return null;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void pwshとアプリで同じmdになる(bool embed)
        {
            string scripts = FindSkillScripts(), pwsh = FindPwsh();
            if (scripts == null || pwsh == null) { log.WriteLine("pwsh かスキルのフォルダが見つからないので確かめていません"); return; }

            string outPs = Path.Combine(s.Root, "ps"), outCs = Path.Combine(s.Root, "cs");
            var psi = new ProcessStartInfo(pwsh) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            var args = new List<string> { "-NoProfile", "-File", Path.Combine(scripts, "New-OperationReport.ps1"), "-SessionDir", s.Dir,
                "-Template", "user,psr,ai,text", "-ReportDir", outPs };
            if (embed) { args.Add("-ImageStyle"); args.Add("Embed"); }
            args.ForEach(psi.ArgumentList.Add);
            using (var p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd(), stderr = p.StandardError.ReadToEnd();
                Assert.True(p.WaitForExit(120000), "pwsh が終わりません");
                Assert.True(p.ExitCode == 0, "pwsh の終了コード " + p.ExitCode + ": " + stderr + stdout);
            }

            var mds = new List<string>();
            RecordingController.Export(Path.Combine(scripts, "..", "templates"), s.Dir, new[] { "user", "psr", "ai", "text" }, outCs, embed,
                true, false, null, null, mds, new List<string>());

            foreach (var cs in mds)
            {
                string ps = Path.Combine(outPs, Path.GetFileName(cs));
                Assert.True(File.Exists(ps), "pwsh 側に無い: " + ps);
                // ai テンプレートの「起動コマンド」など、出力先のフォルダ名だけが違う行は比べない
                string Norm(string t) => t.Replace(outPs, "<OUT>").Replace(outCs, "<OUT>");
                Assert.Equal(Norm(File.ReadAllText(cs)), Norm(File.ReadAllText(ps)));
            }
        }
    }
}
