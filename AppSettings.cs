using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpRec;

namespace OperationRecorderApp
{
    // 前回の設定。%APPDATA%\OperationRecorder\settings.json に保存し、次に起動したときの初期値にする
    public sealed class AppSettings
    {
        public string Caption { get; set; } = "";
        public string Description { get; set; } = "";

        public string Mode { get; set; } = "App";            // Window / App / Active / Desktop / AllDesktops
        // 記録する対象の決め方(Window / App / Desktop):
        //   pick       … 記録を始めてから最初にクリックしたウィンドウ(Desktop はそれが映っているディスプレイ)
        //   foreground … 記録開始のキーを押したときに前面にあるウィンドウ(Desktop はそれが映っているディスプレイ)
        //   list       … 一覧から選んだウィンドウ(Desktop は番号で選んだディスプレイ)
        public string Target { get; set; }
        public const string TargetPick = "pick", TargetForeground = "foreground", TargetList = "list";
        public bool Pick { get; set; } = true;               // v1.2 までの設定(Target が無いときに読み替える)。今は Target と同じ内容を書く

        public string WindowTitle { get; set; } = "";        // Window / App で、一覧から選んだウィンドウのタイトル
        public string ProcessName { get; set; } = "";
        public bool AllSameProcess { get; set; }             // App で、同じ名前のプロセスのウィンドウをすべて対象にする
        public int MonitorIndex { get; set; } = 1;

        public int MarkerRadius { get; set; } = 14;          // クリック位置の丸の半径(出力するときに描く)
        // 出力するときに、操作前の画像に合成する印
        public bool MarkPointer { get; set; }                // マウスポインタ
        public bool MarkCircle { get; set; } = true;         // クリック位置の丸
        public bool MarkArrow { get; set; } = true;          // ドラッグの矢印
        public bool MarkFrame { get; set; } = true;          // 操作した部品の枠
        public MarkOptions ToMarkOptions() =>
            new MarkOptions { Pointer = MarkPointer, Circle = MarkCircle, Arrow = MarkArrow, Frame = MarkFrame, Radius = Math.Max(0, MarkerRadius) };
        public bool StartNow { get; set; }                   // v1.2 までの設定(今は使わない。「記録を開始」はすぐ始め、開始のキーはいつでも受け付ける)
        public HotkeySet Hotkeys { get; set; } = HotkeySet.Defaults();   // 記録の操作キー(「ショートカットキー」タブ)

        // 撮影の設定
        public Dictionary<string, CaptureRule> Rules { get; set; } = CaptureRules.Defaults();
        public int BeforeMs { get; set; } = 200;             // 操作前: 操作した瞬間の何 ms 前の画面を使うか
        public int AfterMs { get; set; } = 500;              // 操作後: 操作が終わってから何 ms 後の画面を使うか
        public bool ShowOverlay { get; set; } = true;        // 記録中に撮影ログのオーバーレイを出す
        public bool OverlayFlee { get; set; } = true;        // オーバーレイが、マウスポインタのあるウィンドウから逃げる
        public bool ShowTargetFrame { get; set; } = true;    // 記録の対象になっているウィンドウを枠で囲う
        public bool FlashOnCapture { get; set; } = true;     // 撮影した瞬間に、枠を光らせ、クリック位置に丸を出す
        public int OverlayX { get; set; } = int.MinValue;    // オーバーレイの位置(未設定なら画面の右下)
        public int OverlayY { get; set; } = int.MinValue;

        // 出力
        public List<string> Templates { get; set; } = new List<string> { "user" };

        public bool EmbedImages { get; set; }
        public bool OutputMd { get; set; } = true;
        public bool OutputHtml { get; set; } = true;
        public bool OpenAfterSave { get; set; }              // 保存したら md を開く
        public bool OpenHtmlAfterSave { get; set; } = true;  // 保存したら HTML を開く
        public bool OpenFolderAfterSave { get; set; }        // 保存したら記録フォルダを開く
        // 保存したら開くか(作らない形式は開かない)
        public bool OpensHtml => OutputHtml && OpenHtmlAfterSave;
        public bool OpensMd => OutputMd && OpenAfterSave;
        public string OutDir { get; set; } = DefaultOutDir;
        public string ReportDir { get; set; } = "";
        public string TemplateDir { get; set; } = "";         // 空ならアプリと同じ場所の templates

        public int SettingsVersion { get; set; }              // 設定ファイルの版(古い版の既定値を直すのに使う)
        public bool AskedShortcut { get; set; }               // 初回起動時にスタートメニュー登録を聞いたか

        public static string DefaultOutDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OperationRecords");

        public static string SettingsDirectoryOverride { get; set; }
        public static bool Passive { get; set; }
        public static string SettingsDir => SettingsDirectoryOverride ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OperationRecorder");

        static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

        static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public string EffectiveTemplateDir =>
            string.IsNullOrWhiteSpace(TemplateDir) ? Path.Combine(AppContext.BaseDirectory, "templates") : TemplateDir;

        public CaptureRule Rule(string kind)
        {
            CaptureRule r;
            if (Rules == null) Rules = CaptureRules.Defaults();
            if (!Rules.TryGetValue(kind, out r) || r == null)
            {
                // 設定ファイルに無い種類は、その種類の既定にする(A〜Z などは既定で記録しない)
                CaptureRule d;
                r = CaptureRules.Defaults().TryGetValue(kind, out d) ? d : new CaptureRule();
                Rules[kind] = r;
            }
            return r;
        }

        public static AppSettings Load()
        {
            string json = null;
            try { if (File.Exists(SettingsPath)) json = File.ReadAllText(SettingsPath, Encoding.UTF8); } catch { }
            var settings = FromJson(json);
            // 試験用指定では既存の記録や利用者のショートカットへ触れない。
            if (SettingsDirectoryOverride != null)
            {
                settings.OutDir = Path.Combine(SettingsDir, "records");
                settings.ReportDir = Path.Combine(SettingsDir, "reports");
                settings.AskedShortcut = true;
            }
            return settings;
        }

        // 設定ファイルの中身から読む(null・壊れた中身なら既定値)。前の版の設定は今の版に直す。自動テストからも呼ぶ
        public static AppSettings FromJson(string json)
        {
            AppSettings s = null;
            try { if (!string.IsNullOrWhiteSpace(json)) s = JsonSerializer.Deserialize<AppSettings>(json, Json); } catch { }
            return Migrate(s ?? new AppSettings());
        }

        public string ToJson() => JsonSerializer.Serialize(this, Json);

        static AppSettings Migrate(AppSettings s)
        {
            if (s.Templates == null) s.Templates = new List<string> { "user" };
            if (s.Rules == null) s.Rules = CaptureRules.Defaults();
            foreach (var k in CaptureRules.Order) s.Rule(k);   // 前の版の設定ファイルには無い項目を補う
            // v1.3 から、キーはグループに分け、どれも既定で記録しない(それまでの「操作キー」は既定で記録していた)
            if (s.SettingsVersion < 4)
                foreach (var g in CaptureRules.KeyGroups) s.Rule(g).Record = false;
            // v1.1 までの user-table / user-before は、画像の並べ方の設定に置き換わった
            if (s.Templates.Contains("user-table") || s.Templates.Contains("user-before"))
            {
                s.Templates.RemoveAll(t => t == "user-table" || t == "user-before");
                if (!s.Templates.Contains("user")) s.Templates.Insert(0, "user");
            }
            // v1.2 から、マウスホイールは既定で記録しない(v1.1 までの既定は「記録する」だった)
            if (s.SettingsVersion < 2)
            {
                s.Rule(CaptureRules.MouseWheel).Record = false;
            }
            // v1.3 から、保存したら HTML を開くのを既定にした
            if (s.SettingsVersion < 5) s.OpenHtmlAfterSave = true;
            s.SettingsVersion = 5;
            // 撮影モード。知らない値は App
            if (!Enum.TryParse(s.Mode ?? "", false, out CaptureMode mode)) s.Mode = "App";
            // 対象の決め方。v1.2 までの Pick(true / false)を読み替える
            if (s.Target != TargetPick && s.Target != TargetForeground && s.Target != TargetList)
                s.Target = s.Pick ? TargetPick : TargetList;
            s.Pick = s.Target == TargetPick;
            // 操作キー。無い・壊れている(重なっている・修飾キーが無いなど)ときは既定に戻す
            if (s.Hotkeys == null || HotkeySet.Order.Any(a => s.Hotkeys.Get(a) == null) || s.Hotkeys.Validate().Count > 0)
                s.Hotkeys = HotkeySet.Defaults();
            return s;
        }

        public void Save()
        {
            Pick = Target == TargetPick;
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath, ToJson(), new UTF8Encoding(false));
        }
    }
}
