using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace OperationRecorderApp
{
    // メイン画面。タブは「記録対象」「撮影の設定」「出力の設定」「ショートカットキー」「記録一覧」「使い方」「設定」。
    // ウィンドウを閉じてもタスクトレイに残り、開始のキー(既定 Ctrl+Shift+F9)でいつでも記録を始められる(終了はトレイのメニューから)
    public sealed partial class MainForm : Form
    {
        readonly AppSettings settings = AppSettings.Load();
        readonly RecordingController controller;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        readonly StatusStrip status = new StatusStrip();
        // Spring の既定(AutoSize = true)のままだと、文章が幅より長いときに項目ごと隠れて何も表示されない
        readonly ToolStripStatusLabel statusLabel = new ToolStripStatusLabel { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
        readonly bool startRecordingOnLaunch;
        bool startHidden;                 // --startup: 最初の表示をしない(タスクトレイにだけ出す)
        InstanceSignal instanceSignal;    // 2 つ目に起動したアプリからの「画面を出して」「記録を始めて」
        bool exiting, trayHintShown;
        string pendingSelectSession;

        ToolStripMenuItem miOpen, miStart, miUndo, miRestart, miStop, miExit;
        TabPage pageRecord, pageCapture, pageOutput, pageHotkeys, pageSessions, pageHelp, pageSettings;
        readonly RecordingOverlay overlay = new RecordingOverlay();
        readonly TargetFrame targetFrame = new TargetFrame();
        readonly ClickRipple ripple = new ClickRipple();
        readonly System.Windows.Forms.Timer frameTimer = new System.Windows.Forms.Timer { Interval = 100 };
        int overlaySteps;                 // オーバーレイに出す手順番号(取消で 1 つ戻す)
        int overlayTitleTick;
        RecState lastState = RecState.Idle;

        public MainForm(bool recordOnLaunch, bool startHidden = false)
        {
            startRecordingOnLaunch = recordOnLaunch;
            this.startHidden = startHidden && !recordOnLaunch;
            Text = WindowTitle.Create();
            Icon = AppIcons.Idle;
            Font = new Font("Yu Gothic UI", 9.5f);
            MinimumSize = new Size(1000, 680);
            Size = new Size(1380, 880);
            StartPosition = FormStartPosition.CenterScreen;

            pageRecord = new TabPage("記録対象") { Padding = new Padding(8) };
            pageCapture = new TabPage("撮影の設定") { Padding = new Padding(8) };
            pageOutput = new TabPage("出力の設定") { Padding = new Padding(8) };
            pageHotkeys = new TabPage("ショートカットキー") { Padding = new Padding(8) };
            pageSessions = new TabPage("記録一覧・編集・出力") { Padding = new Padding(8) };
            pageHelp = new TabPage("使い方") { Padding = new Padding(8) };
            pageSettings = new TabPage("設定") { Padding = new Padding(8) };
            tabs.TabPages.AddRange(new[] { pageRecord, pageCapture, pageOutput, pageHotkeys, pageSessions, pageHelp, pageSettings });
            status.Items.Add(statusLabel);
            status.SizeChanged += (s, e) => statusLabel.Width = Math.Max(100, status.Width - 24);
            // ドッキングは後から加えたものから順に決まるので、帯(上)・状態(下)をタブより後に加える
            Controls.Add(tabs);
            Controls.Add(BuildStartBar());
            Controls.Add(status);

            BuildRecordTab();
            BuildCaptureTab();
            BuildOutputTab();
            BuildHotkeysTab();
            BuildSessionsTab();
            BuildHelpTab();
            BuildSettingsTab();

            controller = new RecordingController(() => settings);
            controller.StateChanged += OnStateChanged;
            controller.Notify += (title, text, icon) => tray.ShowBalloonTip(5000, title, text, icon);
            controller.Saved += OnSaved;
            controller.StepSaved += (line, dir) =>
            {
                overlaySteps++;
                overlay.AddStep(line, dir, overlaySteps);
                overlay.FitHeight(overlaySteps);
                UpdateOverlayStatus();
            };
            controller.StepRemoved += seq =>
            {
                overlay.RemoveStep(seq);
                overlaySteps = Math.Max(0, overlaySteps - 1);
                UpdateOverlayStatus();
            };
            overlay.UndoClicked += () => controller.RequestUndo();
            overlay.RestartClicked += () => controller.RequestRestart();
            overlay.StopClicked += () => controller.RequestStop();
            overlay.OpenMainClicked += () => ShowMain();
            overlay.MovedByUser += p => { settings.OverlayX = p.X; settings.OverlayY = p.Y; };
            // 撮影した瞬間に知らせる(枠を光らせ、クリック位置に丸を出す)
            controller.StepCaptured += (kind, pt) =>
            {
                if (!settings.FlashOnCapture) return;
                if (targetFrame.Visible) targetFrame.Flash();
                if (pt != null) ripple.Play(pt.Value, kind);
            };
            // 記録の対象を枠で囲う(前面のウィンドウが変わったり動いたりするので、0.1 秒ごとに追いかける)
            frameTimer.Tick += (s, e) =>
            {
                var rec = controller.Current;
                if (controller.State == RecState.Recording && settings.ShowTargetFrame && rec != null) targetFrame.SetTarget(rec.CurrentTargetRect());
                else targetFrame.SetTarget(null);
                // オーバーレイのタイトル(記録の対象)。「最初にクリックしたもの」は選ばれた時点で名前に変わる。0.5 秒ごとに見る
                if (++overlayTitleTick % 5 == 0 && overlay.Visible)
                    overlay.SetTarget(controller.State == RecState.Recording && rec != null ? rec.TargetDescription() : null);
            };
            frameTimer.Start();

            BuildTray();
            OnStateChanged();

            tabs.SelectedIndexChanged += (s, e) => { if (tabs.SelectedTab == pageSessions) RefreshSessions(); };
        }

        // --startup のときは、最初の表示だけ止める(ハンドルは作るので、ホットキー・合図・トレイはそのまま動く)
        protected override void SetVisibleCore(bool value)
        {
            if (startHidden && value)
            {
                startHidden = false;
                if (!IsHandleCreated) CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (instanceSignal != null) return;
            try
            {
                instanceSignal = new InstanceSignal(
                    () => BeginInvoke(new Action(ShowMain)),
                    () => BeginInvoke(new Action(() => { if (controller.State == RecState.Idle) controller.Begin(); })));
            }
            catch { }   // 合図を受け取れなくても、アプリとしては動く
            // 旧スタートアップlnkは利用者の設定として保持し、自動修正しない。
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            MaybeOfferShortcut();
            // 起動直後はまだ開いていなかったウィンドウもあるので、画面が出たところで一覧を取り直す
            RefreshWindowList();
            if (startRecordingOnLaunch)
            {
                Hide();
                controller.Begin();
            }
        }

        // 初回起動時(または exe を別の場所へ移した後)に、スタートメニューへの登録を勧める
        void MaybeOfferShortcut()
        {
            if (AppSettings.Passive || AppSettings.SettingsDirectoryOverride != null) return;
            bool moved = ShortcutInstaller.IsInstalled && !ShortcutInstaller.PointsToThisExe();
            if (settings.AskedShortcut && !moved) return;
            string msg = moved
                ? "スタートメニューのショートカットが、別の場所の操作記録ツールを指しています。\nこの場所のツールで登録し直しますか?"
                : "スタートメニューに「操作記録ツール」を登録しますか?\n(このユーザーのスタートメニューだけに登録します。後から「設定」タブで解除できます)";
            if (Confirm(msg, MessageBoxIcon.Question))
            {
                try { ShortcutInstaller.Install(); }
                catch (Exception ex) { ShowWarning("登録できませんでした: " + ex.Message); }
            }
            settings.AskedShortcut = true;
            settings.Save();
            UpdateShortcutStatus();
        }

        // ---------- タスクトレイ ----------

        void BuildTray()
        {
            miOpen = new ToolStripMenuItem("操作記録ツールを開く", null, (s, e) => ShowMain());
            miStart = new ToolStripMenuItem("", null, (s, e) => controller.Begin());
            miUndo = new ToolStripMenuItem("", null, (s, e) => controller.RequestUndo());
            miRestart = new ToolStripMenuItem("", null, (s, e) => controller.RequestRestart());
            miStop = new ToolStripMenuItem("", null, (s, e) => controller.RequestStop());
            miExit = new ToolStripMenuItem("操作記録ツールを終了", null, (s, e) => ExitApp());
            miOpen.Font = new Font(miOpen.Font, FontStyle.Bold);
            trayMenu.Items.AddRange(new ToolStripItem[] { miOpen, new ToolStripSeparator(), miStart, miUndo, miRestart, miStop, new ToolStripSeparator(), miExit });
            tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += (s, e) => ShowMain();
            tray.BalloonTipClicked += (s, e) => ShowMain();
            tray.Visible = true;
            RefreshHotkeyTexts();
        }

        // 操作キーの表示を、今の設定に合わせる(起動時と、「ショートカットキー」タブで変えたとき)
        void RefreshHotkeyTexts()
        {
            var k = settings.Hotkeys;
            if (miStart != null)
            {
                miStart.Text = UiTexts.MenuStart(k);
                miUndo.Text = UiTexts.MenuUndo(k);
                miRestart.Text = UiTexts.MenuRestart(k);
                miStop.Text = UiTexts.MenuStop(k);
            }
            RefreshRecordTabHotkeyTexts();
            if (lblShortcutMenuNote != null) lblShortcutMenuNote.Text = UiTexts.ShortcutMenuNote(k);
            if (controller != null) OnStateChanged();
        }

        void ShowMain()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            if (pendingSelectSession != null) { tabs.SelectedTab = pageSessions; RefreshSessions(pendingSelectSession); pendingSelectSession = null; }
        }

        internal void ShowManaged() => ShowMain();

        internal string ManagementStopBlockReason => ManagementStopPolicy.Reason(controller.State,
            Application.OpenForms.Cast<Form>().Any(form => form.Visible && form.Modal)) ??
            (HasUnsavedSessionEdits ? "編集中の名前・解説・手順の文章を保存してから停止してください。" : null);

        internal void StopManaged()
        {
            // 受理応答から終了実行までに記録が始まった場合も保護する。
            if (ManagementStopBlockReason != null) return;
            exiting = true;
            Close();
        }

        void ExitApp()
        {
            if (controller.State == RecState.Recording || controller.State == RecState.Saving)
            {
                if (!Confirm("記録中です。今の記録は保存されません。終了しますか?")) return;
            }
            exiting = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // ×ボタンではトレイに残す(開始のキーで記録を始められるように)
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!trayHintShown)
                {
                    tray.ShowBalloonTip(4000, "操作記録ツールはタスクトレイにあります",
                        UiTexts.TrayHint(settings.Hotkeys), ToolTipIcon.Info);
                    trayHintShown = true;
                }
                return;
            }
            SaveRecordSettings();
            instanceSignal?.Dispose();
            controller.Dispose();
            overlay.Dispose();
            frameTimer.Stop();
            targetFrame.Dispose();
            ripple.Dispose();
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosing(e);
        }

        // ---------- 状態の表示 ----------

        void OnStateChanged()
        {
            var st = controller.State;
            tray.Icon = st == RecState.Recording || st == RecState.Saving ? AppIcons.Recording : AppIcons.Idle;
            statusLabel.Text = UiTexts.Status(st, settings.Hotkeys);
            tray.Text = UiTexts.TrayTip(st, settings.Hotkeys);
            miStart.Enabled = st == RecState.Idle;
            miUndo.Enabled = miRestart.Enabled = miStop.Enabled = st == RecState.Recording;
            UpdateRecordButtons();
            // 記録するときはこの画面を隠す(記録に写り込まないように)
            if (st == RecState.Recording) Hide();

            // 撮影ログのオーバーレイ。新しい記録が始まったら一覧を空にする
            if (st == RecState.Recording && lastState != RecState.Recording) { overlay.ClearItems(); overlaySteps = 0; overlay.FitHeight(0); }
            lastState = st;
            if (settings.ShowOverlay && st != RecState.Idle)
            {
                UpdateOverlayStatus();
                overlay.FleeEnabled = settings.OverlayFlee;
                if (!overlay.Visible) overlay.ShowAt(settings.OverlayX, settings.OverlayY);
            }
            else overlay.HideOverlay();
        }

        void UpdateOverlayStatus()
        {
            overlay.SetShortcuts(UiTexts.OverlayShortcuts(settings.Hotkeys));
            if (controller.State == RecState.Recording && controller.Current != null) overlay.SetTarget(controller.Current.TargetDescription());
            switch (controller.State)
            {
                case RecState.Recording: overlay.SetStatus("記録中 " + overlaySteps + " 手", Color.FromArgb(255, 90, 90), true); break;
                case RecState.Saving: overlay.SetStatus("保存しています…", Color.FromArgb(255, 180, 60), false); break;
            }
        }

        void OnSaved(string sessionDir, List<string> mds, List<string> htmls)
        {
            if (settings.OpensMd) foreach (var md in mds) OpenPath(md);
            if (settings.OpensHtml) foreach (var h in htmls) OpenPath(h);
            if (settings.OpenFolderAfterSave) OpenPath(sessionDir);
            pendingSelectSession = sessionDir;
            if (Visible) ShowMain();
        }

        // ---------- 共通の部品 ----------

        // ---------- メッセージ ----------

        const string AppTitle = "操作記録ツール";

        void ShowInfo(string text) => MessageBox.Show(this, text, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
        void ShowWarning(string text) => MessageBox.Show(this, text, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        // 「はい」を選んだら true
        bool Confirm(string text, MessageBoxIcon icon = MessageBoxIcon.Warning) => MessageBox.Show(this, text, AppTitle, MessageBoxButtons.YesNo, icon) == DialogResult.Yes;
        // 「OK」を選んだら true
        bool ConfirmOk(string text) => MessageBox.Show(this, text, AppTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;

        internal static void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show("開けませんでした: " + path + "\n" + ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        static Label SubCaption(string text) =>
            new Label { Text = text, AutoSize = true, Margin = new Padding(3, 6, 3, 2), ForeColor = Color.FromArgb(30, 60, 120) };

        static Label Caption(string text, bool bold = false) =>
            new Label { Text = text, AutoSize = true, Margin = new Padding(3, 8, 3, 3), Font = bold ? new Font("Yu Gothic UI", 9.5f, FontStyle.Bold) : null };

        // 説明文。maxWidth で折り返す(列の幅より広いと右が切れる)
        static Label Note(string text, int maxWidth = 460) =>
            new Label { Text = text, AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 0, 3, 6), MaximumSize = new Size(maxWidth, 0) };

        static string BrowseFolder(IWin32Window owner, string current, string description)
        {
            using (var dlg = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true, ShowNewFolderButton = true })
            {
                if (Directory.Exists(current)) dlg.SelectedPath = current;
                return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.SelectedPath : null;
            }
        }

        // テンプレート一覧(templates フォルダの md)。説明は各テンプレート冒頭コメントの 3 行目
        List<OutputChoices.TemplateItem> LoadTemplates() => OutputChoices.LoadTemplates(settings.EffectiveTemplateDir);

        static void FillTemplateList(CheckedListBox box, List<OutputChoices.TemplateItem> items, ICollection<string> selected)
        {
            box.Items.Clear();
            foreach (var t in items) box.Items.Add(t, selected.Contains(t.Name));
            // 全部(5 個まで)が見える高さにする。横スクロールバーの分も足す
            int rows = Math.Max(2, Math.Min(5, box.Items.Count));
            box.Height = box.ItemHeight * rows + SystemInformation.HorizontalScrollBarHeight + 6;
        }

        static List<string> CheckedTemplates(CheckedListBox box)
        {
            var r = new List<string>();
            foreach (OutputChoices.TemplateItem t in box.CheckedItems) r.Add(t.Name);
            return r;
        }
    }
}
