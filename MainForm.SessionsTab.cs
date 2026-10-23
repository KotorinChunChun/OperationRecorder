using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using OpRec;

namespace OperationRecorderApp
{
    // 「記録一覧・編集・出力」タブ: 過去の記録を選び、手順と画像(操作前・操作後を横並び)を確認し、
    // 手順の文章や名前・解説を直して、md / HTML を出力する
    public sealed partial class MainForm
    {
        TextBox txtRecordsDir, txtSesCaption, txtSesDescription, txtSesReportDir, txtStepHeadline, txtStepBody;
        ListView lvSessions;
        ListBox lbSteps;
        PictureBox picBefore, picAfter;
        RadioButton rbSesLink, rbSesEmbed;
        CheckedListBox clbSesTemplates;
        Label lblSesInfo, lblStepInfo, lblStepEdited;
        Button btnSaveMeta, btnOutHtml, btnOutMd, btnOpenSaveFolder, btnDeleteSession, btnSaveStep, btnResetStep, btnDeleteStep, btnEditTemplates, btnMerge;
        List<ReportGenerator.StepInfo> currentSteps = new List<ReportGenerator.StepInfo>();

        bool HasUnsavedSessionEdits
        {
            get
            {
                var session = SelectedSession;
                var step = SelectedStep;
                return session != null && (txtSesCaption.Text.Trim() != (session.Caption ?? "").Trim() ||
                    txtSesDescription.Text.Trim() != (session.Description ?? "").Trim()) ||
                    step != null && (txtStepHeadline.Text.Trim() != (step.Headline ?? "").Trim() ||
                    txtStepBody.Text.Replace("\r\n", "\n").Trim() != (step.Body ?? "").Replace("\r\n", "\n").Trim());
            }
        }

        sealed class SessionItem
        {
            public string Dir, Name, Caption, Description, Start, End, Mode, Target;
            public int Steps;
            public List<string> Outputs = new List<string>();   // 記録フォルダにある md / HTML
        }

        void BuildSessionsTab()
        {
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
            top.Controls.Add(new Label { Text = "記録を探すフォルダ:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
            txtRecordsDir = new TextBox { Width = 520, Text = settings.OutDir };
            var btnBrowse = BrowseButton(txtRecordsDir, "記録が保存されているフォルダ");
            var btnReload = new Button { Text = "一覧を更新", AutoSize = true };
            btnReload.Click += (s, e) => RefreshSessions();
            txtRecordsDir.Leave += (s, e) => RefreshSessions();
            top.Controls.AddRange(new Control[] { txtRecordsDir, btnBrowse, btnReload });

            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            pageSessions.Controls.Add(split);
            pageSessions.Controls.Add(top);
            // SplitterDistance は、実際の幅が決まる前に設定すると小さく丸められるので、表示されてから設定する
            bool splitSized = false;
            split.SizeChanged += (s, e) =>
            {
                if (splitSized || split.Width < 700) return;
                split.SplitterDistance = 340;
                splitSized = true;
            };

            // 左: 記録の一覧
            lvSessions = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
            lvSessions.Columns.Add("記録日時", 145);
            lvSessions.Columns.Add("名前", 100);
            lvSessions.Columns.Add("手順", 45, HorizontalAlignment.Right);
            lvSessions.Columns.Add("出力", 45, HorizontalAlignment.Right);
            lvSessions.SelectedIndexChanged += (s, e) => LoadSelectedSession();
            split.Panel1.Controls.Add(lvSessions);

            // 右: 詳細(中央 = 記録の情報と出力 / 右 = 手順・文章の編集・画像)
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            split.Panel2.Controls.Add(right);

            var info = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            right.Controls.Add(info, 0, 0);
            lblSesInfo = Note("左の一覧から記録を選んでください。", 280);
            info.Controls.Add(lblSesInfo);
            info.Controls.Add(Caption("見出しの名前", true));
            txtSesCaption = new TextBox { Width = 280, Name = "txtSesCaption" };
            info.Controls.Add(txtSesCaption);
            info.Controls.Add(Caption("冒頭に載せる解説", true));
            txtSesDescription = new TextBox { Width = 280, Height = 60, Multiline = true, ScrollBars = ScrollBars.Vertical };
            info.Controls.Add(txtSesDescription);
            btnSaveMeta = new Button { Text = "名前・解説を記録に保存", AutoSize = true };
            btnSaveMeta.Click += (s, e) => SaveSessionMeta();
            info.Controls.Add(btnSaveMeta);

            info.Controls.Add(Caption("出力する報告書", true));
            info.Controls.Add(SubCaption("文章の種類(テンプレート。複数選べます)"));
            clbSesTemplates = new CheckedListBox { Width = 280, Height = 64, CheckOnClick = true, HorizontalScrollbar = true };
            info.Controls.Add(clbSesTemplates);
            btnEditTemplates = new Button { Text = "テンプレートを編集…", AutoSize = true };
            btnEditTemplates.Click += (s, e) => OpenTemplateEditor();
            info.Controls.Add(btnEditTemplates);
            info.Controls.Add(SubCaption("画像"));
            rbSesLink = new RadioButton { Text = "PNG を参照", AutoSize = true, Checked = !settings.EmbedImages };
            rbSesEmbed = new RadioButton { Text = "base64 で埋込", AutoSize = true, Checked = settings.EmbedImages, Margin = new Padding(12, 3, 3, 3) };
            var imgPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            imgPanel.Controls.AddRange(new Control[] { rbSesLink, rbSesEmbed });
            info.Controls.Add(imgPanel);
            info.Controls.Add(Caption("保存するフォルダ(空なら記録フォルダの中)", true));
            txtSesReportDir = new TextBox { Width = 200 };
            info.Controls.Add(Row(txtSesReportDir, BrowseButton(txtSesReportDir, "md / HTML を保存するフォルダ")));
            // 出力したら、すぐ開く
            btnOutHtml = new Button { Text = "HTML で出力", Width = 134, Height = 34, Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold) };
            btnOutHtml.Click += (s, e) => GenerateForSelected(true);
            btnOutMd = new Button { Text = "Markdown で出力", Width = 134, Height = 34, Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold) };
            btnOutMd.Click += (s, e) => GenerateForSelected(false);
            info.Controls.Add(Row(btnOutHtml, btnOutMd));
            btnOpenSaveFolder = new Button { Text = "保存フォルダを開く", AutoSize = true };
            btnOpenSaveFolder.Click += (s, e) => { if (SelectedSession != null) OpenPath(SaveFolderOf(SelectedSession)); };
            info.Controls.Add(btnOpenSaveFolder);
            btnMerge = new Button { Text = "ほかの記録と合成…", AutoSize = true, Margin = new Padding(3, 12, 3, 3) };
            btnMerge.Click += (s, e) => OpenMerge();
            info.Controls.Add(btnMerge);
            btnDeleteSession = new Button { Text = "この記録を削除(ごみ箱へ)", AutoSize = true, ForeColor = Color.DarkRed, Margin = new Padding(3, 16, 3, 3) };
            btnDeleteSession.Click += (s, e) => DeleteSelectedSession();
            info.Controls.Add(btnDeleteSession);

            // 右端: 手順の一覧・文章の編集・画像(操作前と操作後を横に並べる)
            var stepCol = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            stepCol.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 見出し「手順」
            stepCol.RowStyles.Add(new RowStyle(SizeType.Percent, 30));     // 手順の一覧
            stepCol.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 文章の編集
            stepCol.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 画像の見出し
            stepCol.RowStyles.Add(new RowStyle(SizeType.Percent, 70));     // 画像
            right.Controls.Add(stepCol, 1, 0);
            stepCol.Controls.Add(Caption("手順(選ぶと、文章の編集と画像の確認ができます)", true), 0, 0);
            lbSteps = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true, IntegralHeight = false };
            lbSteps.SelectedIndexChanged += (s, e) => ShowSelectedStep();
            stepCol.Controls.Add(lbSteps, 0, 1);

            var editor = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            editor.Controls.Add(new Label { Text = "見出し", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, 0, 0);
            txtStepHeadline = new TextBox { Dock = DockStyle.Fill, Name = "txtStepHeadline" };
            editor.Controls.Add(txtStepHeadline, 1, 0);
            editor.Controls.Add(new Label { Text = "本文", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, 0, 1);
            txtStepBody = new TextBox { Dock = DockStyle.Fill, Name = "txtStepBody", Multiline = true, Height = 54, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
            editor.Controls.Add(txtStepBody, 1, 1);
            btnSaveStep = new Button { Text = "この手順の文章を保存", AutoSize = true };
            btnSaveStep.Click += (s, e) => SaveStepText(false);
            btnResetStep = new Button { Text = "自動の文章に戻す", AutoSize = true };
            btnResetStep.Click += (s, e) => SaveStepText(true);
            btnDeleteStep = new Button { Text = "この手順を削除", AutoSize = true, ForeColor = Color.DarkRed, Margin = new Padding(18, 3, 3, 3) };
            btnDeleteStep.Click += (s, e) => DeleteSelectedStep();
            lblStepEdited = new Label { AutoSize = true, ForeColor = Color.DarkGreen, Margin = new Padding(8, 8, 3, 3) };
            var editBtns = Row(btnSaveStep, btnResetStep, btnDeleteStep, lblStepEdited);
            editBtns.WrapContents = true;   // 幅が足りないときは折り返す(はみ出して列を押し広げないように)
            editBtns.AutoSize = true;
            editBtns.Dock = DockStyle.Fill;
            editor.Controls.Add(editBtns, 1, 2);
            stepCol.Controls.Add(editor, 0, 2);

            lblStepInfo = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 6, 3, 3) };
            // 説明は列の幅で折り返す(固定幅だと、ウィンドウが狭いときに右が切れる)
            stepCol.SizeChanged += (s, e) => lblStepInfo.MaximumSize = new Size(Math.Max(200, stepCol.Width - 12), 0);
            stepCol.Controls.Add(lblStepInfo, 0, 3);
            var pics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
            pics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            pics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            pics.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            pics.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pics.Controls.Add(new Label { Text = "操作前", AutoSize = true, Font = new Font("Yu Gothic UI", 9.5f, FontStyle.Bold) }, 0, 0);
            pics.Controls.Add(new Label { Text = "操作後", AutoSize = true, Font = new Font("Yu Gothic UI", 9.5f, FontStyle.Bold) }, 1, 0);
            picBefore = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White };
            picAfter = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White };
            picBefore.DoubleClick += (s, e) => { var st = SelectedStep; if (st?.BeforePath != null) OpenPath(st.BeforePath); };
            picAfter.DoubleClick += (s, e) => { var st = SelectedStep; if (st?.AfterPath != null) OpenPath(st.AfterPath); };
            pics.Controls.Add(picBefore, 0, 1);
            pics.Controls.Add(picAfter, 1, 1);
            stepCol.Controls.Add(pics, 0, 4);

            FillTemplateList(clbSesTemplates, LoadTemplates(), settings.Templates);
            EnableSessionButtons(false);
            EnableStepEditor(false);
        }

        SessionItem SelectedSession => lvSessions.SelectedItems.Count > 0 ? (SessionItem)lvSessions.SelectedItems[0].Tag : null;

        ReportGenerator.StepInfo SelectedStep =>
            lbSteps.SelectedIndex >= 0 && lbSteps.SelectedIndex < currentSteps.Count ? currentSteps[lbSteps.SelectedIndex] : null;

        void EnableSessionButtons(bool on)
        {
            foreach (var c in new Control[] { txtSesCaption, txtSesDescription, btnSaveMeta, btnOutHtml, btnOutMd, btnOpenSaveFolder, btnMerge, btnDeleteSession })
                c.Enabled = on;
        }

        void EnableStepEditor(bool on)
        {
            foreach (var c in new Control[] { txtStepHeadline, txtStepBody, btnSaveStep, btnResetStep, btnDeleteStep }) c.Enabled = on;
            if (!on) { txtStepHeadline.Text = txtStepBody.Text = lblStepEdited.Text = ""; }
        }

        void RefreshSessions(string select = null)
        {
            string keep = select ?? SelectedSession?.Dir;
            lvSessions.BeginUpdate();
            lvSessions.Items.Clear();
            string root = txtRecordsDir.Text.Trim();
            if (Directory.Exists(root))
            {
                foreach (var dir in Directory.GetDirectories(root, "session-*").OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal))
                {
                    var it = ReadSession(dir);
                    if (it == null) continue;
                    var lvi = new ListViewItem(new[] { it.Start, it.Caption, it.Steps.ToString(), it.Outputs.Count.ToString() }) { Tag = it };
                    lvSessions.Items.Add(lvi);
                    if (keep != null && string.Equals(it.Dir, keep, StringComparison.OrdinalIgnoreCase)) lvi.Selected = true;
                }
            }
            lvSessions.EndUpdate();
            if (lvSessions.SelectedItems.Count > 0) lvSessions.SelectedItems[0].EnsureVisible();
            else ClearSessionDetail();
        }

        static SessionItem ReadSession(string dir)
        {
            string jsonPath = Path.Combine(dir, "session.json");
            if (!File.Exists(jsonPath)) return null;
            try
            {
                var j = JsonNode.Parse(File.ReadAllText(jsonPath, Encoding.UTF8)).AsObject();
                string name = Path.GetFileName(dir);
                var it = new SessionItem
                {
                    Dir = dir, Name = name,
                    Caption = (string)j["caption"] ?? "",
                    Description = (string)j["description"] ?? "",
                    Start = ((string)j["start"] ?? "").Replace('T', ' '),
                    End = ((string)j["end"] ?? "").Replace('T', ' '),
                    Mode = (string)j["mode"] ?? "",
                };
                it.Target = ReportGenerator.TargetText(j);
                string ev = Path.Combine(dir, "events.jsonl");
                it.Steps = File.Exists(ev) ? File.ReadAllLines(ev, Encoding.UTF8).Count(l => l.Trim().Length > 0) : 0;
                it.Outputs = Directory.GetFiles(dir, name + "_*.md").Concat(Directory.GetFiles(dir, name + "_*.html"))
                    .Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                return it;
            }
            catch { return null; }
        }

        void ClearSessionDetail()
        {
            lblSesInfo.Text = "左の一覧から記録を選んでください。";
            txtSesCaption.Text = txtSesDescription.Text = "";
            lbSteps.Items.Clear();
            SetPictures(null, null);
            currentSteps.Clear();
            EnableSessionButtons(false);
            EnableStepEditor(false);
        }

        void LoadSelectedSession()
        {
            var it = SelectedSession;
            if (it == null) { ClearSessionDetail(); return; }
            EnableSessionButtons(true);
            lblSesInfo.Text = "撮影モード: " + it.Mode + " / 対象: " + it.Target;   // 日時・名前・手順数は左の一覧で分かるので出さない
            txtSesCaption.Text = it.Caption;
            txtSesDescription.Text = it.Description;
            ReloadSteps(0);
        }

        void ReloadSteps(int selectIndex)
        {
            var it = SelectedSession;
            lbSteps.Items.Clear();
            try { currentSteps = ReportGenerator.ListSteps(it.Dir); }
            catch (Exception ex) { currentSteps = new List<ReportGenerator.StepInfo>(); lblSesInfo.Text += "\n(手順を読めませんでした: " + ex.Message + ")"; }
            foreach (var st in currentSteps)
                lbSteps.Items.Add("手順 " + st.Number + ". " + (string.IsNullOrEmpty(st.Headline) ? "(画面キャプチャ)" : st.Headline) + (st.Edited ? "  ✎" : ""));
            if (lbSteps.Items.Count > 0) lbSteps.SelectedIndex = Math.Max(0, Math.Min(selectIndex, lbSteps.Items.Count - 1));
            else { SetPictures(null, null); EnableStepEditor(false); }
        }

        void ShowSelectedStep()
        {
            var st = SelectedStep;
            if (st == null) { EnableStepEditor(false); SetPictures(null, null); lblStepInfo.Text = ""; return; }
            EnableStepEditor(true);
            txtStepHeadline.Text = st.Headline ?? "";
            txtStepBody.Text = (st.Body ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
            lblStepEdited.Text = st.Edited ? "✎ 手直し済み" : "";
            // 記録した値(ステップ記録ツールと同じ項目)
            var info = new List<string> { "日時: " + st.TimeText + (st.Screen != null ? "   座標: 画面 " + st.Screen + " / ウィンドウ内 " + st.PointInWindow : "") + (string.IsNullOrEmpty(st.ClassName) ? "" : "   クラス名: " + st.ClassName) };
            info.Add("画面: " + st.WindowTitle);
            if (!string.IsNullOrEmpty(st.Program)) info.Add("プログラム: " + st.Program);
            if (!string.IsNullOrEmpty(st.UiElements)) info.Add("UI 要素: " + st.UiElements);

            lblStepInfo.Text = string.Join("\n", info);
            SetPictures(st.BeforePath, st.AfterPath);
            if (st.BeforeMarks != null && picBefore.Image != null)
            {
                SaveRecordSettings();   // 「出力の設定」タブで変えた印をすぐ反映する
                var marked = new Bitmap(picBefore.Image);
                using (var g = Graphics.FromImage(marked)) MarkPainter.Draw(g, marked.Size, st.BeforeMarks, settings.ToMarkOptions());
                var old = picBefore.Image;
                picBefore.Image = marked;
                old.Dispose();
            }
        }

        void SetPictures(string before, string after)
        {
            SetPicture(picBefore, before);
            SetPicture(picAfter, after);
        }

        // ファイルを掴んだままにしない(記録の削除・画像の編集ができるように)ため、メモリに読み込んでから表示する
        static void SetPicture(PictureBox box, string path)
        {
            var old = box.Image;
            box.Image = null;
            old?.Dispose();
            if (path != null && File.Exists(path))
                box.Image = Image.FromStream(new MemoryStream(File.ReadAllBytes(path)));
        }

        // 手順の見出し・本文を直す(reset = true なら自動の文章に戻す)。記録フォルダの edits.json に保存する
        void SaveStepText(bool reset)
        {
            var it = SelectedSession;
            var st = SelectedStep;
            if (it == null || st == null) return;
            int index = lbSteps.SelectedIndex;
            try
            {
                if (reset) ReportGenerator.SaveStepEdit(it.Dir, st.Seq, null, null);
                else
                {
                    string head = txtStepHeadline.Text.Trim();
                    string body = txtStepBody.Text.Replace("\r\n", "\n").Trim();
                    ReportGenerator.SaveStepEdit(it.Dir, st.Seq,
                        head == (st.OriginalHeadline ?? "") ? null : head,
                        body == (st.OriginalBody ?? "").Trim() ? null : body);
                }
                ReloadSteps(index);
                statusLabel.Text = reset ? "手順 " + st.Number + " を自動の文章に戻しました。" : "手順 " + st.Number + " の文章を保存しました。「出力する」で md / HTML に反映されます。";
            }
            catch (Exception ex) { ShowWarning("保存できませんでした: " + ex.Message); }
        }

        // 選んでいる手順を記録から消す(後ろの手順の番号は 1 つずつ繰り上がる)。画像はごみ箱へ移す
        void DeleteSelectedStep()
        {
            var it = SelectedSession;
            var st = SelectedStep;
            if (it == null || st == null) return;
            if (!Confirm("手順 " + st.Number + ". " + st.Headline + "\n\nを記録から削除します。後ろの手順の番号は 1 つずつ繰り上がります。\n(この手順の画像はごみ箱へ移します)よろしいですか?")) return;
            int index = lbSteps.SelectedIndex;
            try
            {
                SetPictures(null, null);   // 表示中の画像を手放してから消す
                SessionEditor.DeleteStep(it.Dir, st.Seq, RecycleBin.SendFile);
                RefreshSessions(it.Dir);
                ReloadSteps(index);
                statusLabel.Text = "手順 " + st.Number + " を削除しました。「出力する」で md / HTML に反映されます。";
            }
            catch (Exception ex) { ShowWarning("削除できませんでした: " + ex.Message); }
        }

        // テンプレートのエディタ。選んでいる記録があれば、それに当てはめてプレビューできる
        // 選んでいる記録(A)に、取り直した記録(B)を合成して、新しい記録を作る
        void OpenMerge()
        {
            var it = SelectedSession;
            if (it == null) return;
            var others = lvSessions.Items.Cast<ListViewItem>().Select(i => (SessionItem)i.Tag).Where(o => o.Dir != it.Dir)
                .Select(o => (o.Dir, o.Start + "  " + o.Caption + "(" + o.Steps + " 手順)")).ToList();
            if (others.Count == 0) { ShowInfo("合成する相手の記録がありません。同じフォルダに、取り直した記録を保存してください。"); return; }
            using (var f = new MergeForm(it.Dir, others))
                if (f.ShowDialog(this) == DialogResult.OK && f.MergedDir != null)
                {
                    RefreshSessions(f.MergedDir);
                    statusLabel.Text = "合成した記録を作りました: " + Path.GetFileName(f.MergedDir);
                }
        }

        void OpenTemplateEditor()
        {
            using (var f = new TemplateEditorForm(settings.EffectiveTemplateDir, SelectedSession?.Dir))
                f.ShowDialog(this);
            var items = LoadTemplates();
            var keep = CheckedTemplates(clbSesTemplates);
            FillTemplateList(clbSesTemplates, items, keep);
            FillTemplateList(clbTemplates, items, settings.Templates);
        }

        void SaveSessionMeta()
        {
            var it = SelectedSession;
            if (it == null) return;
            try
            {
                string jsonPath = Path.Combine(it.Dir, "session.json");
                var j = JsonNode.Parse(File.ReadAllText(jsonPath, Encoding.UTF8)).AsObject();
                j["caption"] = txtSesCaption.Text.Trim();
                j["description"] = txtSesDescription.Text.Trim();
                File.WriteAllText(jsonPath, j.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
                RefreshSessions(it.Dir);
                statusLabel.Text = "名前・解説を保存しました: " + it.Name;
            }
            catch (Exception ex) { ShowWarning("保存できませんでした: " + ex.Message); }
        }

        // 出力するフォルダ(指定が無ければ記録フォルダ)
        string SaveFolderOf(SessionItem it) => string.IsNullOrWhiteSpace(txtSesReportDir.Text) ? it.Dir : txtSesReportDir.Text.Trim();

        // html = true なら HTML、false なら Markdown で出力し、出力したものをすぐ開く
        void GenerateForSelected(bool html)
        {
            var it = SelectedSession;
            if (it == null) return;
            var tpl = CheckedTemplates(clbSesTemplates);
            if (tpl.Count == 0) { ShowInfo("出力するテンプレートを 1 つ以上選んでください。"); return; }
            try
            {
                Cursor = Cursors.WaitCursor;
                var mds = new List<string>();
                var htmls = new List<string>();
                RecordingController.Export(settings, it.Dir, tpl, txtSesReportDir.Text.Trim(), rbSesEmbed.Checked, !html, html,
                    txtSesCaption.Text.Trim(), txtSesDescription.Text.Trim(), mds, htmls);
                var outs = html ? htmls : mds;
                RefreshSessions(it.Dir);
                statusLabel.Text = "出力しました: " + string.Join(", ", outs.Select(Path.GetFileName));
                foreach (var o in outs) OpenPath(o);
            }
            catch (Exception ex) { ShowWarning("出力できませんでした: " + ex.Message); }
            finally { Cursor = Cursors.Default; }
        }
        void DeleteSelectedSession()
        {
            var it = SelectedSession;
            if (it == null) return;
            if (!Confirm("この記録をごみ箱へ移します。\n\n" + it.Dir + "\n\nよろしいですか?(ごみ箱から元に戻せます)")) return;
            try
            {
                SetPictures(null, null);
                RecycleBin.SendDirectory(it.Dir);
                RefreshSessions();
            }
            catch (Exception ex) { ShowWarning("削除できませんでした: " + ex.Message); }
        }
    }
}
