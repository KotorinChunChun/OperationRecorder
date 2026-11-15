using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using OpRec;

namespace OperationRecorderApp
{
    sealed class DocumentExporter
    {
        readonly string edge, pandoc;

        public DocumentExporter(bool pdf, bool docx, string pandocPath)
        {
            if (pdf)
                edge = new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData }
                    .Select(Environment.GetFolderPath).Where(folder => !string.IsNullOrEmpty(folder))
                    .Select(folder => Path.Combine(folder, "Microsoft", "Edge", "Application", "msedge.exe"))
                    .FirstOrDefault(File.Exists)
                    ?? throw new InvalidOperationException("PDF出力にはMicrosoft Edgeが必要です。");
            if (docx) pandoc = FindPandoc(pandocPath);
        }

        static string FindPandoc(string specified)
        {
            if (!string.IsNullOrWhiteSpace(specified))
            {
                string executable = specified.Trim();
                if (Path.IsPathFullyQualified(executable) && File.Exists(executable))
                    return executable;
                throw new InvalidOperationException("指定されたPandocの実行ファイルが見つかりません。");
            }

            foreach (string directory in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
                     .Where(directory => directory.Length != 0).Select(directory => Path.Combine(directory, "Pandoc"))
                     .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)))
            {
                var folder = directory.Trim().Trim('"');
                if (!Path.IsPathFullyQualified(folder)) continue;
                string candidate = Path.Combine(folder, "pandoc.exe");
                if (File.Exists(candidate)) return candidate;
            }
            throw new InvalidOperationException("DOCX出力にはPandocが必要です。「出力の設定」でpandoc.exeを指定するか、PATHに追加してください。");
        }

        public List<string> Export(ReportGenerator.Options options)
        {
            options.EmbedImages = true;
            options.RestrictImagePaths = true;
            var reports = ReportGenerator.Render(options);
            var output = new List<(string Temporary, string Destination)>();
            var workspace = Directory.CreateTempSubdirectory("oprec-doc-");
            string temporary = workspace.FullName;
            try
            {
                string input = Path.Combine(temporary, "report.html");
                foreach (var report in reports)
                {
                    File.WriteAllText(input, HtmlExporter.ToDocumentHtml(report.Value), new UTF8Encoding(false));
                    if (edge != null)
                    {
                        string pdf = Path.Combine(temporary, output.Count + ".pdf");
                        Run(edge, temporary, "PDF", "--headless", "--no-first-run", "--no-default-browser-check",
                            "--disable-background-networking", "--disable-extensions", "--disable-sync", "--enable-logging=stderr",
                            "--user-data-dir=" + Path.Combine(temporary, "edge-" + output.Count), "--no-pdf-header-footer",
                            "--print-to-pdf=" + pdf, new Uri(input).AbsoluteUri);
                        WaitForPdf(pdf);
                        output.Add((pdf, Path.ChangeExtension(report.Key, ".pdf")));
                    }
                    if (pandoc != null)
                    {
                        string docx = Path.Combine(temporary, output.Count + ".docx");
                        Run(pandoc, temporary, "DOCX", "--sandbox", "--from=html+raw_html", "--to=docx", "--output=" + docx, input);
                        using (var archive = ZipFile.OpenRead(docx))
                        {
                            if (archive.GetEntry("word/document.xml") == null || archive.GetEntry("[Content_Types].xml") == null)
                                throw new IOException("DOCXの形式が不正です。");
                        }
                        output.Add((docx, Path.ChangeExtension(report.Key, ".docx")));
                    }
                }
                foreach (var file in output)
                {
                    // 一時作業先とは別のドライブにも出力できるよう、同じフォルダで置き換える。
                    string pending = Path.Combine(Path.GetDirectoryName(file.Destination), ".oprec-" + Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        File.Copy(file.Temporary, pending);
                        File.Move(pending, file.Destination, true);
                    }
                    finally { File.Delete(pending); }
                }
                return output.Select(file => file.Destination).ToList();
            }
            finally
            {
                if (string.Equals(workspace.Parent.FullName, Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase))
                {
                    for (int attempt = 0; attempt < 10; attempt++)
                    {
                        try
                        {
                            if ((File.GetAttributes(temporary) & FileAttributes.ReparsePoint) == 0) Directory.Delete(temporary, true);
                            break;
                        }
                        catch (DirectoryNotFoundException) { break; }
                        catch (IOException) { Thread.Sleep(100); }
                        catch (UnauthorizedAccessException) { Thread.Sleep(100); }
                    }
                }
            }
        }

        static void WaitForPdf(string path)
        {
            // Edgeの起動プロセスが先に終了する場合があるため、出力の終端を確認する。
            var clock = Stopwatch.StartNew();
            Span<byte> signature = stackalloc byte[5];
            Span<byte> ending = stackalloc byte[1024];
            while (clock.ElapsedMilliseconds < 20000)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    long length = stream.Length;
                    if (length > 5)
                    {
                        stream.ReadExactly(signature);
                        if (!signature.SequenceEqual("%PDF-"u8)) throw new InvalidDataException("PDFの形式が不正です。");
                        int count = (int)Math.Min(length, ending.Length);
                        stream.Position = length - count;
                        stream.ReadExactly(ending.Slice(0, count));
                        if (ending.Slice(0, count).LastIndexOf("%%EOF"u8) >= 0) return;
                    }
                }
                catch (IOException) { }
                Thread.Sleep(100);
            }
            throw new TimeoutException("Microsoft EdgeのPDF出力を20秒以内に確認できませんでした。EdgeでPDFの印刷が許可されているか確認してください。");
        }

        static void Run(string executable, string work, string format, params string[] arguments)
        {
            var info = new ProcessStartInfo(executable)
            {
                CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = work,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new IOException(format + "の変換を開始できません。");
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                throw new TimeoutException(format + "の変換が60秒以内に完了しませんでした。");
            }
            string errors = stderr.Wait(2000) ? stderr.Result : "";
            stdout.Wait(2000);
            if (process.ExitCode != 0)
            {
                string reason = errors.Trim().Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "";
                if (reason.Length > 320) reason = reason.Substring(0, 320);
                throw new IOException(format + "の変換に失敗しました (" + process.ExitCode + ") " + reason);
            }
        }
    }
}
