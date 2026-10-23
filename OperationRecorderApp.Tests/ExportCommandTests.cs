using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace OperationRecorderApp.Tests
{
    // OperationRecorder.exe --export(画面を出さない出力)
    public class ExportCommandTests : IDisposable
    {
        readonly TestSession s = new TestSession();
        public void Dispose() => s.Dispose();

        [Fact]
        public void 引数を読む()
        {
            var o = ExportCommand.Parse(new[] { "--export", s.Dir, "--template", "user, psr", "--format", "html", "--embed", "--out", "D:\\x" }, out string error);
            Assert.Null(error);
            Assert.Equal(s.Dir, o.SessionDir);
            Assert.Equal(new[] { "user", "psr" }, o.Templates);

            Assert.True(o.Embed);
            Assert.True(o.Html);
            Assert.False(o.Md);
            Assert.Equal("D:\\x", o.OutDir);
        }

        [Fact]
        public void 省略時はuserでmdとHTMLの両方()
        {
            var o = ExportCommand.Parse(new[] { "--export", s.Dir }, out _);
            Assert.False(o.Embed);
            Assert.True(o.Md && o.Html);
            Assert.Equal(new[] { "user" }, o.Templates);
        }

        [Theory]
        [InlineData(new[] { "--export" }, "記録フォルダ")]
        [InlineData(new[] { "--export", "a", "--layout", "side" }, "知らない引数")]   // 並べ方はテンプレートで決める
        [InlineData(new[] { "--export", "a", "--format", "pdf" }, "--format")]
        [InlineData(new[] { "--export", "a", "--unknown" }, "知らない引数")]
        [InlineData(new[] { "--export", "a", "b" }, "1 つだけ")]
        [InlineData(new[] { "--export", "a", "--out" }, "値がありません")]
        public void 引数の誤りを知らせる(string[] args, string expected)
        {
            Assert.Null(ExportCommand.Parse(args, out string error));
            Assert.Contains(expected, error);
        }

        [Fact]
        public void 出力先を指定できる()
        {
            string outDir = Path.Combine(s.Root, "out");
            var files = ExportCommand.Run(ExportCommand.Parse(new[] { "--export", s.Dir, "--out", outDir, "--template", "user,ai" }, out _));
            Assert.Equal(4, files.Count);   // md 2 つ + HTML 2 つ
            Assert.All(files, f => Assert.StartsWith(outDir, f));
            Assert.All(files, f => Assert.True(File.Exists(f)));
        }

        [Fact]
        public void 記録フォルダが無ければ失敗する()
        {
            Assert.Throws<DirectoryNotFoundException>(() =>
                ExportCommand.Run(ExportCommand.Parse(new[] { "--export", Path.Combine(s.Root, "nothing") }, out _)));
        }

        // ビルドした exe を実際に起動する(画面は出ない。起動中のアプリがあっても動く)
        [Fact]
        public void exeから出力できる()
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "OperationRecorder.exe");
            Assert.True(File.Exists(exe), "OperationRecorder.exe がテストの出力フォルダにありません: " + exe);
            string result = Path.Combine(s.Root, "result.txt");
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (var a in new[] { "--export", s.Dir, "--template", "user", "--format", "html", "--result", result }) psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                Assert.True(p.WaitForExit(60000), "exe が終わりません");
                Assert.True(p.ExitCode == 0, "終了コード " + p.ExitCode + ": " + stderr);
                string html = File.ReadAllLines(result).Single();
                Assert.EndsWith(".html", html);
                Assert.Contains(html, stdout);
                var text = File.ReadAllText(html);
                Assert.Contains("<table class=\"images\">", text);
            }
        }

        [Fact]
        public void exeは引数の誤りを終了コード2で返す()
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "OperationRecorder.exe");
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            foreach (var a in new[] { "--export", s.Dir, "--format", "pdf" }) psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                Assert.True(p.WaitForExit(60000));
                Assert.Equal(2, p.ExitCode);
            }
        }
    }
}
