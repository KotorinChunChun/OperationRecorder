using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace OperationRecorderApp
{
    // 「使い方」タブ(マニュアルの表示)と「設定」タブ(スタートメニュー登録・テンプレートの場所・管理者で再起動)
    public sealed partial class MainForm
    {
        Label lblShortcut, lblShortcutMenuNote;
        TextBox txtTemplateDir;

        static string ManualPath => Path.Combine(AppContext.BaseDirectory, "MANUAL.md");

        static Label WideNote(string text) => Note(text, 900);

        void BuildHelpTab()
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
            var btnOpenManual = new Button { Text = "マニュアルを別のアプリで開く", AutoSize = true };
            btnOpenManual.Click += (s, e) => OpenPath(ManualPath);
            bar.Controls.Add(btnOpenManual);
            var box = new RichTextBox
            {
                Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White, BorderStyle = BorderStyle.None,
                Font = new Font("Yu Gothic UI", 10f), DetectUrls = false,
            };
            if (File.Exists(ManualPath)) RenderMarkdown(box, File.ReadAllText(ManualPath, Encoding.UTF8));
            else box.Text = "マニュアル(MANUAL.md)が見つかりません: " + ManualPath;
            pageHelp.Controls.Add(box);
            pageHelp.Controls.Add(bar);
        }

        // マニュアル(Markdown)を読みやすく表示する。見出しは太字・大きめ、表は列を区切って並べ、** や ` などの記号は消す
        static void RenderMarkdown(RichTextBox box, string md)
        {
            var baseFont = box.Font;
            var mono = new Font("BIZ UDゴシック", 9.5f);
            foreach (var raw in md.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw;
                Font font = baseFont;
                Color color = Color.Black;
                var h = System.Text.RegularExpressions.Regex.Match(line, @"^(#{1,6})\s+(.*)$");
                if (h.Success)
                {
                    int level = h.Groups[1].Value.Length;
                    font = new Font(baseFont.FontFamily, level == 1 ? 16f : level == 2 ? 13f : 11f, FontStyle.Bold);
                    color = level <= 2 ? Color.FromArgb(30, 60, 120) : Color.Black;
                    line = h.Groups[2].Value;
                    if (level <= 2) box.AppendText("\n");
                }
                else if (line.Trim() == "---") { line = new string('─', 60); color = Color.Silver; }
                else if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^\s*\|[\s:\-|]+\|\s*$")) continue;   // 表の区切り行
                else if (line.TrimStart().StartsWith("|"))
                {
                    var cells = line.Trim().Trim('|').Split('|');
                    line = "  " + string.Join("   │ ", Array.ConvertAll(cells, c => c.Trim()));
                    font = mono;
                }
                else if (line.StartsWith("> ")) { line = "  " + line.Substring(2); color = Color.FromArgb(90, 90, 90); }
                line = line.Replace("**", "").Replace("`", "");
                box.SelectionStart = box.TextLength;
                box.SelectionFont = font;
                box.SelectionColor = color;
                box.AppendText(line + "\n");
            }
            box.SelectionStart = 0;
            box.ScrollToCaret();
        }

        void BuildSettingsTab()
        {
            var p = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            pageSettings.Controls.Add(p);

            p.Controls.Add(Caption("スタートメニュー", true));
            p.Controls.Add(WideNote("このユーザーのスタートメニューに「操作記録ツール」フォルダを作り、次の 2 つを登録します(管理者権限は要りません)。"));
            lblShortcutMenuNote = WideNote(UiTexts.ShortcutMenuNote(settings.Hotkeys));
            p.Controls.Add(lblShortcutMenuNote);
            lblShortcut = new Label { AutoSize = true, Margin = new Padding(3, 3, 3, 6) };
            p.Controls.Add(lblShortcut);
            var btnInstall = new Button { Text = "スタートメニューに登録", AutoSize = true };
            btnInstall.Enabled = !AppSettings.Passive && AppSettings.SettingsDirectoryOverride == null;
            btnInstall.Click += (s, e) =>
            {
                try { ShortcutInstaller.Install(); }
                catch (Exception ex) { ShowWarning("登録できませんでした: " + ex.Message); }
                UpdateShortcutStatus();
            };
            var btnUninstall = new Button { Text = "登録を解除", AutoSize = true };
            btnUninstall.Enabled = btnInstall.Enabled;
            btnUninstall.Click += (s, e) =>
            {
                try { ShortcutInstaller.Uninstall(); }
                catch (Exception ex) { ShowWarning("解除できませんでした: " + ex.Message); }
                UpdateShortcutStatus();
            };
            p.Controls.Add(Row(btnInstall, btnUninstall));

            p.Controls.Add(Caption("Windows の起動時", true));
            p.Controls.Add(WideNote("自動起動は KotorinToys 本体で管理します。対応インストーラーで正式導入した後、本体の「本体設定・自動起動」から設定してください。新規の自動起動はOFFです。\n" +
                (StartupRegistration.IsEnabled()
                    ? "以前のスタートアップ登録があります。重複を避けるため、本体で有効にする前に Windows のスタートアップフォルダーで確認してください。このアプリは既存登録を変更しません。"
                    : "このアプリからスタートアップへの新規登録は行いません。")));

            p.Controls.Add(Caption("テンプレートのフォルダ", true));
            p.Controls.Add(WideNote("報告書(md)の体裁は、このフォルダの md(テンプレート)で決まります。空欄ならアプリと同じ場所の templates フォルダを使います。\n" +
                "書き方と使える変数は、各テンプレートの冒頭のコメントに書いてあります。社内様式などを作るときは、コピーして別のフォルダに置き、ここで指定してください。"));
            txtTemplateDir = new TextBox { Width = 520, Text = settings.TemplateDir };
            var btnTplBrowse = BrowseButton(txtTemplateDir, "テンプレートのフォルダ");
            var btnTplApply = new Button { Text = "反映", AutoSize = true };
            btnTplApply.Click += (s, e) =>
            {
                settings.TemplateDir = txtTemplateDir.Text.Trim();
                settings.Save();
                var items = LoadTemplates();
                FillTemplateList(clbTemplates, items, settings.Templates);
                FillTemplateList(clbSesTemplates, items, settings.Templates);
                statusLabel.Text = "テンプレートのフォルダ: " + settings.EffectiveTemplateDir + "(" + items.Count + " 件)";
            };
            var btnTplOpen = new Button { Text = "フォルダを開く", AutoSize = true };
            btnTplOpen.Click += (s, e) => OpenPath(settings.EffectiveTemplateDir);
            p.Controls.Add(Row(txtTemplateDir, btnTplBrowse, btnTplApply, btnTplOpen));

            p.Controls.Add(Caption("管理者権限", true));
            p.Controls.Add(WideNote("「管理者として実行」したアプリの操作を記録するときは、このツールも管理者権限で動かす必要があります。\n" +
                "(UAC の確認画面そのものは、管理者権限でも記録できません)"));
            var btnElevate = new Button { Text = "管理者として再起動", AutoSize = true };
            btnElevate.Enabled = !AppSettings.Passive && AppSettings.SettingsDirectoryOverride == null;
            btnElevate.Click += (s, e) => RestartAsAdmin();
            p.Controls.Add(btnElevate);

            p.Controls.Add(Caption("このツールについて", true));
            p.Controls.Add(WideNote("操作記録ツール " + Assembly.GetExecutingAssembly().GetName().Version + "\n" +
                "プログラムの場所: " + AppContext.BaseDirectory + "\n設定の保存先: " + AppSettings.SettingsDir));

            UpdateShortcutStatus();
        }

        void UpdateShortcutStatus()
        {
            if (lblShortcut == null) return;
            lblShortcut.Text = ShortcutInstaller.InstallerShortcutPointsToThisExe()
                ? "インストーラーのショートカットが登録されています。追加の登録は不要です。"
                : ShortcutInstaller.IsInstalled
                ? (ShortcutInstaller.PointsToThisExe() ? "登録済みです。" : "登録済みですが、別の場所のツールを指しています。登録し直してください。")
                : "登録されていません。";
        }

        void RestartAsAdmin()
        {
            if (controller.State == RecState.Recording || controller.State == RecState.Saving)
            {
                ShowInfo("記録中は再起動できません。記録を保存してから操作してください。");
                return;
            }
            SaveRecordSettings();
            try
            {
                // 同時に 1 つしか動かせないので、管理者の cmd に 1 秒待ってから起動し直してもらい、その間にこちらは終了する。
                // UAC で取り消されたらここで例外になるので、終了処理はその後(取り消し時にアプリが止まったままにならないように)
                Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 2 >nul & start \"\" \"" + Environment.ProcessPath + "\"")
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch (Exception ex)
            {
                ShowWarning("管理者として再起動できませんでした(UAC で取り消された可能性があります): " + ex.Message);
                return;
            }
            exiting = true;
            Close();
        }
    }
}
