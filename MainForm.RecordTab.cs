using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using OpRec;

namespace OperationRecorderApp
{
    // タブの上の帯(記録を開始・名前・始めるタイミング)、「記録対象」タブ(撮影モード・対象・記録中の表示)、「出力の設定」タブ
    public sealed partial class MainForm
    {
        TextBox txtCaption, txtDescription, txtOutDir, txtReportDir;
        RadioButton rbWindow, rbApp, rbActive, rbDesktop, rbAllDesktops, rbPick, rbForeground, rbChoose, rbLink, rbEmbed;
        ComboBox cmbWindows, cmbMonitors;
        CheckBox chkAllSame, chkOpen, chkOpenHtml, chkOpenFolder, chkOutMd, chkOutHtml;
        NumericUpDown numRadius;
        CheckBox chkMarkPointer, chkMarkCircle, chkMarkArrow, chkMarkFrame;
        CheckedListBox clbTemplates;
        Button btnStart, btnRefreshWindows;
        Label lblNoTarget, lblStartKey;
        Panel pnlWindowTarget, pnlMonitorTarget;

        sealed class WindowItem
        {
            public IntPtr Hwnd; public string Title, Process;
            public override string ToString() => Title + "   [" + Process + ".exe]";
        }

        // ディスプレイの一覧の 1 行。M が null の行は「決め方」(最初にクリック / 前面にある)
        sealed class MonitorItem
        {
            public MonitorInfo M;
            public string Target;     // M が null のとき: AppSettings.TargetPick / TargetForeground
            public string Text;
            public override string ToString() => M != null ? M.Index + ": " + M.Describe() : Text;
        }

        // タブの上の帯: 「記録を開始」・記録の名前(どのタブを開いていても押せる)
        Control BuildStartBar()
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(6, 6, 6, 2) };
            btnStart = new Button { Text = "記録を開始", Width = 170, Height = 36, Font = new Font("Yu Gothic UI", 11f, FontStyle.Bold), Margin = new Padding(3, 3, 18, 3) };
            btnStart.Click += (s, e) => OnStartClicked();
            bar.Controls.Add(btnStart);

            bar.Controls.Add(BarLabel("記録の名前"));
            txtCaption = new TextBox { Width = 260, Text = settings.Caption, Margin = new Padding(3, 9, 18, 3), PlaceholderText = "例: 受注登録(省略可)" };
            bar.Controls.Add(txtCaption);
            lblStartKey = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 12, 3, 3) };
            bar.Controls.Add(lblStartKey);
            return bar;
        }

        static Label BarLabel(string text) =>
            new Label { Text = text, AutoSize = true, Font = new Font("Yu Gothic UI", 9.5f, FontStyle.Bold), Margin = new Padding(3, 12, 3, 3) };

        // 「記録対象」タブ: 撮影モード・記録する対象 / 記録中の画面表示・オーバーレイ・記録を保存するフォルダ
        void BuildRecordTab()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            pageRecord.Controls.Add(root);
            var left = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            var right = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            root.Controls.Add(left, 0, 0);
            root.Controls.Add(right, 1, 0);
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // 左: 何を記録するか(撮影モード・記録する対象)
            left.Controls.Add(Caption("撮影モード", true));
            rbWindow = new RadioButton { Text = "Window … 1 つのウィンドウだけ(ダイアログは記録しない)", AutoSize = true };
            rbApp = new RadioButton { Text = "App … アプリのウィンドウ全部(ダイアログ・メニューも含む)", AutoSize = true };
            rbActive = new RadioButton { Text = "Active … 操作したウィンドウをその都度", AutoSize = true };
            rbDesktop = new RadioButton { Text = "Desktop … 1 つのディスプレイ全体(対象のウィンドウがあるディスプレイなど)", AutoSize = true };
            rbAllDesktops = new RadioButton { Text = "AllDesktops … PC のすべてのディスプレイを合わせた全体", AutoSize = true };
            var modePanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            modePanel.Controls.AddRange(new Control[] { rbWindow, rbApp, rbActive, rbDesktop, rbAllDesktops });
            left.Controls.Add(modePanel);

            left.Controls.Add(Caption("記録する対象", true));
            pnlWindowTarget = new Panel { Width = 480, Height = 146 };
            rbPick = new RadioButton { Text = "記録を始めてから、最初にクリックしたものにする", AutoSize = true, Location = new Point(0, 0) };
            rbForeground = new RadioButton { Text = "", AutoSize = true, Location = new Point(0, 26) };
            rbChoose = new RadioButton { Text = "一覧から選ぶ:", AutoSize = true, Location = new Point(0, 52) };
            cmbWindows = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(20, 78), Width = 340 };
            btnRefreshWindows = new Button { Text = "一覧を更新", Location = new Point(366, 77), AutoSize = true };
            chkAllSame = new CheckBox { Text = "同じ名前のアプリのウィンドウをすべて対象にする(App のみ)", AutoSize = true, Location = new Point(20, 110) };
            pnlWindowTarget.Controls.AddRange(new Control[] { rbPick, rbForeground, rbChoose, cmbWindows, btnRefreshWindows, chkAllSame });
            left.Controls.Add(pnlWindowTarget);
            pnlMonitorTarget = new Panel { Width = 480, Height = 40, Visible = false };
            cmbMonitors = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(0, 4), Width = 470 };
            pnlMonitorTarget.Controls.Add(cmbMonitors);
            left.Controls.Add(pnlMonitorTarget);
            lblNoTarget = Note("このモードでは、対象を選ぶ必要はありません。");
            left.Controls.Add(lblNoTarget);

            // 右: 記録中の画面表示・オーバーレイ・記録を保存するフォルダ
            right.Controls.Add(Caption("記録中の画面表示", true));
            chkTargetFrame = new CheckBox { Text = "記録の対象になっているウィンドウを赤い枠で囲う", AutoSize = true, Checked = settings.ShowTargetFrame };
            chkFlash = new CheckBox { Text = "撮影した瞬間に知らせる(枠が黄色く光り、クリックした位置に丸が広がる)", AutoSize = true, Checked = settings.FlashOnCapture };
            right.Controls.Add(chkTargetFrame);
            right.Controls.Add(chkFlash);
            chkOverlay = new CheckBox { Text = "撮影ログのオーバーレイ(対象・使えるキー・直近 10 手・操作ボタン)を表示する", AutoSize = true, Checked = settings.ShowOverlay };
            right.Controls.Add(chkOverlay);
            chkOverlayFlee = new CheckBox { Text = "ポインタがあるウィンドウに重なったら、画面の隅へ自動で移動する", AutoSize = true, Checked = settings.OverlayFlee, Margin = new Padding(24, 3, 3, 3) };
            chkOverlay.CheckedChanged += (s, e) => chkOverlayFlee.Enabled = chkOverlay.Checked;
            chkOverlayFlee.Enabled = chkOverlay.Checked;
            right.Controls.Add(chkOverlayFlee);

            right.Controls.Add(Caption("記録を保存するフォルダ", true));
            right.Controls.Add(Note("この中に「session-日時_名前」というフォルダを作り、画像と記録データを保存します。"));
            txtOutDir = new TextBox { Width = 400, Text = settings.OutDir };
            right.Controls.Add(Row(txtOutDir, BrowseButton(txtOutDir, "記録を保存するフォルダ")));
            right.Controls.Add(Note("記録を始めると、この画面はタスクトレイに隠れます。記録する画面と入力した文字(パスワード欄を除く)が保存されるので、写ってほしくない画面は閉じておいてください。"));

            // 初期値
            switch (settings.Mode)
            {
                case "Window": rbWindow.Checked = true; break;
                case "Active": rbActive.Checked = true; break;
                case "Desktop": rbDesktop.Checked = true; break;
                case "AllDesktops": rbAllDesktops.Checked = true; break;
                default: rbApp.Checked = true; break;
            }
            rbPick.Checked = settings.Target == AppSettings.TargetPick;
            rbForeground.Checked = settings.Target == AppSettings.TargetForeground;
            rbChoose.Checked = settings.Target == AppSettings.TargetList;
            chkAllSame.Checked = settings.AllSameProcess;
            foreach (var rb in new[] { rbWindow, rbApp, rbActive, rbDesktop, rbAllDesktops }) rb.CheckedChanged += (s, e) => UpdateTargetPanels();
            foreach (var rb in new[] { rbPick, rbForeground, rbChoose }) rb.CheckedChanged += (s, e) => UpdateTargetPanels();
            cmbMonitors.SelectedIndexChanged += (s, e) => UpdateTargetPanels();
            btnRefreshWindows.Click += (s, e) => RefreshWindowList();
            cmbWindows.DropDown += (s, e) => { if (cmbWindows.Items.Count == 0) RefreshWindowList(); };
            RefreshWindowList();
            RefreshMonitorList();
            RefreshRecordTabHotkeyTexts();
            UpdateTargetPanels();
        }

        static CheckBox FormatBox(bool on) => new CheckBox { Checked = on, AutoSize = true, Margin = new Padding(30, 7, 6, 6) };

        // 「作ったら開く」は、その形式を作るときだけ押せる(作らないものは開けない)
        static void BindCreateOpen(CheckBox create, CheckBox open)
        {
            open.Enabled = create.Checked;
            create.CheckedChanged += (s, e) => { open.Enabled = create.Checked; if (!create.Checked) open.Checked = false; };
        }

        // 「出力の設定」タブ: 報告書の冒頭に載せる解説・出力する報告書・保存したら開くもの・md / HTML を保存するフォルダ
        void BuildOutputTab()
        {
            var p = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            pageOutput.Controls.Add(p);
            p.Controls.Add(Caption("報告書の冒頭に載せる解説(省略可)", true));
            txtDescription = new TextBox { Width = 560, Height = 60, Multiline = true, ScrollBars = ScrollBars.Vertical, Text = settings.Description };
            p.Controls.Add(txtDescription);

            p.Controls.Add(Caption("保存したときに出力する報告書", true));
            p.Controls.Add(SubCaption("① 文章の種類(テンプレート。複数選ぶと、その数だけ出力)"));
            clbTemplates = new CheckedListBox { Width = 560, Height = 64, CheckOnClick = true, HorizontalScrollbar = true };
            p.Controls.Add(clbTemplates);
            p.Controls.Add(SubCaption("② ファイルの形式"));
            var formats = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, CellBorderStyle = TableLayoutPanelCellBorderStyle.Single, Margin = new Padding(3, 3, 3, 6) };
            formats.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            formats.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            formats.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            foreach (var h in new[] { "形式", "作る", "作ったら開く" })
                formats.Controls.Add(new Label { Text = h, AutoSize = true, Font = new Font("Yu Gothic UI", 9.5f, FontStyle.Bold), Margin = new Padding(6) });
            chkOutHtml = FormatBox(settings.OutputHtml);
            chkOpenHtml = FormatBox(settings.OpenHtmlAfterSave);
            chkOutMd = FormatBox(settings.OutputMd);
            chkOpen = FormatBox(settings.OpenAfterSave);
            formats.Controls.Add(new Label { Text = "HTML(ブラウザで読める)", AutoSize = true, Margin = new Padding(6, 8, 6, 6) });
            formats.Controls.Add(chkOutHtml);
            formats.Controls.Add(chkOpenHtml);
            formats.Controls.Add(new Label { Text = "md(Markdown)", AutoSize = true, Margin = new Padding(6, 8, 6, 6) });
            formats.Controls.Add(chkOutMd);
            formats.Controls.Add(chkOpen);
            p.Controls.Add(formats);
            BindCreateOpen(chkOutHtml, chkOpenHtml);
            BindCreateOpen(chkOutMd, chkOpen);
            chkOpenFolder = new CheckBox { Text = "保存したら記録フォルダを開く", AutoSize = true, Checked = settings.OpenFolderAfterSave };
            p.Controls.Add(chkOpenFolder);
            rbLink = new RadioButton { Text = "PNG を参照(img フォルダの画像を使う。ファイルが軽い)", AutoSize = true, Checked = !settings.EmbedImages };
            rbEmbed = new RadioButton { Text = "base64 で埋込(1 ファイルで完結する)", AutoSize = true, Checked = settings.EmbedImages };
            var imgPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true };
            imgPanel.Controls.AddRange(new Control[] { rbLink, rbEmbed });
            p.Controls.Add(imgPanel);

            p.Controls.Add(Caption("画像に描く印(操作前の画像に、出力するときに合成する)", true));
            chkMarkPointer = new CheckBox { Text = "マウスポインタ", AutoSize = true, Checked = settings.MarkPointer };
            chkMarkCircle = new CheckBox { Text = "クリック位置の丸(左 = 赤 / 右 = 青 / 中 = 緑)", AutoSize = true, Checked = settings.MarkCircle };
            numRadius = new NumericUpDown { Minimum = 1, Maximum = 200, Value = Math.Max(1, Math.Min(200, settings.MarkerRadius)), Width = 70 };
            chkMarkArrow = new CheckBox { Text = "ドラッグの移動先への矢印", AutoSize = true, Checked = settings.MarkArrow };
            chkMarkFrame = new CheckBox { Text = "操作した部品の枠", AutoSize = true, Checked = settings.MarkFrame };
            p.Controls.Add(chkMarkPointer);
            p.Controls.Add(Row(chkMarkCircle, new Label { Text = "半径", AutoSize = true, Margin = new Padding(12, 6, 3, 3) }, numRadius,
                new Label { Text = "px", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }));
            p.Controls.Add(chkMarkArrow);
            p.Controls.Add(chkMarkFrame);
            chkMarkCircle.CheckedChanged += (s, e) => numRadius.Enabled = chkMarkCircle.Checked;
            numRadius.Enabled = chkMarkCircle.Checked;


            p.Controls.Add(Caption("md / HTML を保存するフォルダ(空なら記録フォルダの中)", true));
            txtReportDir = new TextBox { Width = 460, Text = settings.ReportDir };
            p.Controls.Add(Row(txtReportDir, BrowseButton(txtReportDir, "md / HTML を保存するフォルダ")));
            FillTemplateList(clbTemplates, LoadTemplates(), settings.Templates);
        }

        // 操作キーの設定で変わる文字(記録を始めるタイミング・対象の決め方)
        void RefreshRecordTabHotkeyTexts()
        {
            if (lblStartKey == null) return;
            var k = settings.Hotkeys;
            lblStartKey.Text = "(" + k.Start + " でも、いつでも始められます)";
            rbForeground.Text = UiTexts.ForegroundWindowOption(k);

            foreach (var o in cmbMonitors.Items)
                if (o is MonitorItem mi && mi.Target == AppSettings.TargetForeground) mi.Text = UiTexts.ForegroundMonitorOption(k);
            cmbMonitors.Invalidate();
            // コンボボックスの表示中の文字は、項目を入れ直さないと変わらない
            int sel = cmbMonitors.SelectedIndex;
            var items = cmbMonitors.Items.Cast<object>().ToArray();
            cmbMonitors.Items.Clear();
            cmbMonitors.Items.AddRange(items);
            cmbMonitors.SelectedIndex = Math.Min(Math.Max(sel, 0), cmbMonitors.Items.Count - 1);
        }

        // 選んでいる「対象の決め方」(Window / App はラジオボタン、Desktop はディスプレイの一覧)
        string SelectedTarget
        {
            get
            {
                if (SelectedMode == "Desktop")
                {
                    var mi = cmbMonitors.SelectedItem as MonitorItem;
                    return mi == null || mi.M == null ? (mi?.Target ?? AppSettings.TargetPick) : AppSettings.TargetList;
                }
                return rbForeground.Checked ? AppSettings.TargetForeground : rbChoose.Checked ? AppSettings.TargetList : AppSettings.TargetPick;
            }
        }

        static FlowLayoutPanel Row(params Control[] cs)
        {
            var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            p.Controls.AddRange(cs);
            return p;
        }

        Button BrowseButton(TextBox target, string description)
        {
            var b = new Button { Text = "参照…", AutoSize = true };
            b.Click += (s, e) => { var p = BrowseFolder(this, target.Text, description); if (p != null) target.Text = p; };
            return b;
        }

        string SelectedMode => rbWindow.Checked ? "Window" : rbActive.Checked ? "Active" : rbDesktop.Checked ? "Desktop" : rbAllDesktops.Checked ? "AllDesktops" : "App";

        void UpdateTargetPanels()
        {
            string mode = SelectedMode;
            pnlWindowTarget.Visible = mode == "Window" || mode == "App";
            pnlMonitorTarget.Visible = mode == "Desktop";
            lblNoTarget.Visible = mode == "Active" || mode == "AllDesktops";
            chkAllSame.Enabled = mode == "App" && rbChoose.Checked;
            cmbWindows.Enabled = btnRefreshWindows.Enabled = rbChoose.Checked;

        }

        void RefreshWindowList()
        {
            string keep = cmbWindows.SelectedItem is WindowItem w ? w.Title : settings.WindowTitle;
            cmbWindows.Items.Clear();
            int own = Environment.ProcessId;
            foreach (var e in WinUtil.EnumZOrder())
            {
                if (WinUtil.Root(e.H) != e.H || e.Pid == own) continue;
                string title = WinUtil.TextOf(e.H);
                if (string.IsNullOrWhiteSpace(title)) continue;
                if (TargetRules.IsShellWindow(WinUtil.ClassOf(e.H))) continue;
                cmbWindows.Items.Add(new WindowItem { Hwnd = e.H, Title = title, Process = WinUtil.ProcName(e.Pid) ?? "?" });
            }
            foreach (WindowItem it in cmbWindows.Items) if (it.Title == keep) { cmbWindows.SelectedItem = it; break; }
        }

        void RefreshMonitorList()
        {
            cmbMonitors.Items.Clear();
            cmbMonitors.Items.Add(new MonitorItem { Target = AppSettings.TargetPick, Text = "記録を始めてから、最初にクリックしたウィンドウが映っているディスプレイにする" });
            cmbMonitors.Items.Add(new MonitorItem { Target = AppSettings.TargetForeground, Text = UiTexts.ForegroundMonitorOption(settings.Hotkeys) });
            if (settings.Target == AppSettings.TargetForeground) cmbMonitors.SelectedIndex = 1;
            foreach (var m in Monitors.GetAll())
            {
                cmbMonitors.Items.Add(new MonitorItem { M = m });
                if (settings.Target == AppSettings.TargetList && m.Index == settings.MonitorIndex) cmbMonitors.SelectedIndex = cmbMonitors.Items.Count - 1;
            }
            if (cmbMonitors.SelectedIndex < 0) cmbMonitors.SelectedIndex = 0;
        }

        // 画面の内容を設定に反映して保存する
        void SaveRecordSettings()
        {
            settings.Caption = txtCaption.Text.Trim();
            settings.Description = txtDescription.Text.Trim();
            settings.Mode = SelectedMode;
            if (settings.Mode == "Desktop")
            {
                var mi = cmbMonitors.SelectedItem as MonitorItem;
                settings.Target = SelectedTarget;
                if (mi?.M != null) settings.MonitorIndex = mi.M.Index;
            }
            else if (settings.Mode == "Window" || settings.Mode == "App")
            {
                settings.Target = SelectedTarget;
                if (cmbWindows.SelectedItem is WindowItem w) { settings.WindowTitle = w.Title; settings.ProcessName = w.Process; }
                settings.AllSameProcess = chkAllSame.Checked;
            }
            settings.MarkerRadius = (int)numRadius.Value;
            settings.MarkPointer = chkMarkPointer.Checked;
            settings.MarkCircle = chkMarkCircle.Checked;
            settings.MarkArrow = chkMarkArrow.Checked;
            settings.MarkFrame = chkMarkFrame.Checked;
            settings.Templates = CheckedTemplates(clbTemplates);
            settings.EmbedImages = rbEmbed.Checked;
            settings.OpenAfterSave = chkOpen.Checked;
            settings.OpenHtmlAfterSave = chkOpenHtml.Checked;
            settings.OpenFolderAfterSave = chkOpenFolder.Checked;
            settings.OutputMd = chkOutMd.Checked;
            settings.OutputHtml = chkOutHtml.Checked;
            settings.OutDir = string.IsNullOrWhiteSpace(txtOutDir.Text) ? AppSettings.DefaultOutDir : txtOutDir.Text.Trim();
            settings.ReportDir = txtReportDir.Text.Trim();
            SaveCaptureSettings();
            try { settings.Save(); } catch { }
        }

        void OnStartClicked()
        {
            string mode = SelectedMode;
            // 一覧で選ばれていなければ一覧を取り直す(前回選んだウィンドウが開いていれば、それが選ばれる)
            if ((mode == "Window" || mode == "App") && rbChoose.Checked && !(cmbWindows.SelectedItem is WindowItem)) RefreshWindowList();
            if ((mode == "Window" || mode == "App") && rbChoose.Checked && !(cmbWindows.SelectedItem is WindowItem))
            {
                ShowInfo("記録する対象のウィンドウを一覧から選んでください。");
                return;
            }
            if (mode == "Desktop" || mode == "AllDesktops" || mode == "Active")
            {
                if (!ConfirmOk((mode == "Desktop" ? "ディスプレイ全体" : mode == "AllDesktops" ? "すべてのディスプレイ" : "操作したすべてのアプリの画面") +
                    "を記録します。関係ない画面(メール・チャットなど)や、そこで入力した文字も記録に残ります。\n写ってほしくない画面を閉じてから始めてください。続けますか?")) return;
            }
            SaveRecordSettings();
            // この画面を隠してから始める(「前面にあるもの」を対象にするとき、この画面の後ろにあったウィンドウが前面になるのを待つ)
            Hide();
            var t = new System.Windows.Forms.Timer { Interval = 400 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); controller.Begin(); };
            t.Start();
        }

        void UpdateRecordButtons()
        {
            if (btnStart == null) return;
            switch (controller.State)
            {
                case RecState.Recording: btnStart.Text = "記録中…"; btnStart.Enabled = false; break;
                case RecState.Saving: btnStart.Text = "保存中…"; btnStart.Enabled = false; break;
                default: btnStart.Text = "記録を開始"; btnStart.Enabled = true; break;
            }
        }
    }
}
