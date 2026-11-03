using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using OpRec;

namespace OperationRecorderApp
{
    // 画面を出さずに、記録フォルダから md / HTML を出力する(自動テストやバッチ用)
    //   OperationRecorder.exe --export <記録フォルダ> [--template user,psr] [--format html,md] [--embed] [--marks circle,arrow,frame] [--radius 14] [--out <フォルダ>] [--template-dir <フォルダ>]
    //                         [--caption <名前>] [--description <解説>] [--result <ファイル>]
    // 終了コード: 0 = 成功 / 1 = 出力に失敗 / 2 = 引数の誤り。出力したファイルのフルパスを 1 行ずつ標準出力
    // (と --result のファイル)に書く。利用者の設定(settings.json)は読まないので、同じ引数なら必ず同じ結果になる
    public static class ExportCommand
    {
        public sealed class Options
        {
            public string SessionDir;
            public List<string> Templates = new List<string> { "user" };

            public bool Md = true;
            public bool Html = true;
            public bool Embed;
            public MarkOptions Marks = MarkOptions.Default();
            public string OutDir;
            public string TemplateDir;
            public string Caption;          // null なら記録時の名前(session.json)
            public string Description;
            public string ResultFile;
        }


        public const string Usage =
            "使い方: OperationRecorder.exe --export <記録フォルダ> [--template user,psr,ai,text]\n" +
            "        [--format html,md] [--embed] [--marks pointer,circle,arrow,frame|none] [--radius <px>] [--out <フォルダ>] [--template-dir <フォルダ>]\n" +
            "        [--caption <名前>] [--description <解説>] [--result <ファイル>]";

        public static bool IsExport(string[] args) =>
            args.Length > 0 && string.Equals(args[0], "--export", StringComparison.OrdinalIgnoreCase);

        // 引数を読む。誤りがあれば error に理由を入れて null を返す
        public static Options Parse(string[] args, out string error)
        {
            error = null;
            var o = new Options();
            int i = 0;
            if (IsExport(args)) i = 1;
            string Next(string name)
            {
                if (i + 1 >= args.Length) throw new ArgumentException(name + " の値がありません");
                return args[++i];
            }
            try
            {
                for (; i < args.Length; i++)
                {
                    string a = args[i];
                    switch (a.ToLowerInvariant())
                    {
                        case "--template":
                            o.Templates = Next(a).Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
                            if (o.Templates.Count == 0) throw new ArgumentException("--template が空です");
                            break;

                        case "--embed": o.Embed = true; break;
                        case "--marks": o.Marks = MarkOptions.Parse(Next(a), o.Marks.Radius); break;
                        case "--radius":
                            if (!int.TryParse(Next(a), out int radius) || radius < 0 || radius > 200) throw new ArgumentException("--radius は 0〜200 の数です");
                            o.Marks.Radius = radius;
                            break;
                        case "--format":
                            var f = Next(a).ToLowerInvariant().Split(',').Select(t => t.Trim()).ToList();
                            if (f.Any(t => t != "html" && t != "md")) throw new ArgumentException("--format は html / md(カンマ区切りで両方も可)です");
                            o.Html = f.Contains("html"); o.Md = f.Contains("md");
                            break;
                        case "--out": o.OutDir = Next(a); break;
                        case "--template-dir": o.TemplateDir = Next(a); break;
                        case "--caption": o.Caption = Next(a); break;
                        case "--description": o.Description = Next(a); break;
                        case "--result": o.ResultFile = Next(a); break;
                        default:
                            if (a.StartsWith("--")) throw new ArgumentException("知らない引数です: " + a);
                            if (o.SessionDir != null) throw new ArgumentException("記録フォルダは 1 つだけ指定できます: " + a);
                            o.SessionDir = a;
                            break;
                    }
                }
                if (o.SessionDir == null) throw new ArgumentException("記録フォルダを指定してください");
            }
            catch (ArgumentException ex) { error = ex.Message; return null; }
            return o;
        }

        // 出力したファイルのフルパス(md → HTML の順)を返す。失敗したら例外
        public static List<string> Run(Options o)
        {
            string session = Path.GetFullPath(o.SessionDir);
            if (!Directory.Exists(session)) throw new DirectoryNotFoundException("記録フォルダがありません: " + session);
            string tplDir = string.IsNullOrWhiteSpace(o.TemplateDir) ? Path.Combine(AppContext.BaseDirectory, "templates") : Path.GetFullPath(o.TemplateDir);
            var mds = new List<string>();
            var htmls = new List<string>();
            RecordingController.Export(tplDir, session, o.Templates, o.OutDir == null ? null : Path.GetFullPath(o.OutDir),
                o.Embed, o.Md, o.Html, o.Caption, o.Description, mds, htmls, o.Marks);
            return mds.Concat(htmls).ToList();
        }

        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);

        // 標準出力・標準エラーの準備。WinExe なので、リダイレクトされていなければ呼び出し元のコンソールにつなぐ。
        // リダイレクトされているとき(テスト・パイプ)は UTF-8 で書く(既定のままだと日本語のパスが CP932 になる)
        static void SetUpConsole()
        {
            if (GetStdHandle(-11) == IntPtr.Zero) { AttachConsole(-1); return; }
            var utf8 = new UTF8Encoding(false);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        }

        // exe から呼ぶ入口
        public static int Execute(string[] args)
        {
            SetUpConsole();
            var o = Parse(args, out string error);
            if (o == null)
            {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine(Usage);
                return 2;
            }
            var lines = new List<string>();
            int code = 0;
            try
            {
                foreach (var p in Run(o)) { Console.WriteLine(p); lines.Add(p); }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("出力に失敗しました: " + ex.Message);
                lines.Add("ERROR: " + ex.Message);
                code = 1;
            }
            if (!string.IsNullOrEmpty(o.ResultFile))
            {
                try { File.WriteAllLines(Path.GetFullPath(o.ResultFile), lines, new UTF8Encoding(false)); } catch { }
            }
            return code;
        }
    }
}
