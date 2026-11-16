using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace OperationRecorderApp;

internal static class SkillDraftDialog
{
    static BrandedForm Window(string title, int width, int height) => new()
    {
        Text = WindowTitle.Create(title),
        ClientSize = new Size(width, height),
        MinimumSize = new Size(Math.Min(width, 700), 420),
        StartPosition = FormStartPosition.CenterParent,
        ShowInTaskbar = false,
        MinimizeBox = false,
    };

    static FlowLayoutPanel Bar(DockStyle dock = DockStyle.Bottom) => new()
    {
        Dock = dock, AutoSize = true, WrapContents = true, Padding = new Padding(6),
    };

    static TextBox Box(int width, string name, string value = "") => new()
    {
        Width = width, AccessibleName = name, Text = value,
    };

    static Button Button(string text) => new() { Text = text, AutoSize = true };
    static Label Label(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(5, 8, 3, 3) };

    static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        RowHeadersVisible = false, AutoGenerateColumns = false, AllowUserToResizeRows = false,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false,
    };

    static DataGridViewTextBoxColumn TextColumn(string name, int width, bool readOnly = false) =>
        new() { HeaderText = name, Width = width, ReadOnly = readOnly, SortMode = DataGridViewColumnSortMode.NotSortable };

    static DataGridViewCheckBoxColumn CheckColumn(string name) =>
        new() { HeaderText = name, Width = 61, SortMode = DataGridViewColumnSortMode.NotSortable };

    static void Error(Form form, Exception error) =>
        MessageBox.Show(form, error.Message, form.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public static void Analyze(Form owner, string directory)
    {
        var source = new SkillSource(directory);
        using var form = Window("手順案を分析", 1110, 680);
        using var cancel = new CancellationTokenSource();
        form.FormClosed += (_, _) => cancel.Cancel();

        var grid = Grid();
        grid.Columns.AddRange(CheckColumn("送信"), TextColumn("操作番号", 77, true), TextColumn("時刻 ms", 86, true),
            TextColumn("操作・説明", 225), TextColumn("文字起こし", 195), CheckColumn("前画像"), CheckColumn("後画像"));
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.RowTemplate.Height = 47;
        foreach (var choice in source.SelectSteps())
            grid.Rows[grid.Rows.Add(true, choice.Seq, choice.AtMs, choice.Action, choice.Narration, false, false)].Tag = choice;
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        using var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom,
            AccessibleName = "画像の確認とマスク指定", Cursor = Cursors.Cross, BackColor = Color.FromArgb(33, 36, 41) };
        var side = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "画像の種類" };
        side.Items.AddRange(["操作前画像", "操作後画像"]);
        side.SelectedIndex = 0;
        var clear = Button("マスクを解除");
        var pictureTools = Bar(DockStyle.Top);
        pictureTools.Controls.AddRange([side, clear]);
        using var tip = new ToolTip();
        tip.SetToolTip(picture, "ドラッグした長方形を黒く塗りつぶします。");
        var sides = new SplitContainer { Dock = DockStyle.Fill, Width = 1100, Panel2MinSize = 230, SplitterDistance = 720 };
        sides.Panel1.Controls.Add(grid);
        sides.Panel2.Controls.Add(picture);
        sides.Panel2.Controls.Add(pictureTools);

        SkillSelection Selection() => grid.CurrentRow?.Tag as SkillSelection;
        List<SkillMask> Masks(SkillSelection step) => side.SelectedIndex == 0 ? step.BeforeMasks : step.AfterMasks;
        Point? start = null;
        Rectangle drag = Rectangle.Empty;
        void LoadPicture()
        {
            picture.Image?.Dispose();
            picture.Image = null;
            start = null;
            drag = Rectangle.Empty;
            if (Selection() is not { } choice) return;
            try { picture.Image = source.Image(choice.Seq, side.SelectedIndex == 0 ? "before" : "after", Masks(choice)); }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            { Error(form, error); }
        }

        grid.SelectionChanged += (_, _) => LoadPicture();
        side.SelectedIndexChanged += (_, _) => LoadPicture();
        clear.Click += (_, _) => { if (Selection() is { } step) Masks(step).Clear(); LoadPicture(); };
        picture.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left && picture.Image != null) start = e.Location; };
        picture.MouseMove += (_, e) =>
        {
            if (start == null) return;
            drag = FromPoints(start.Value, e.Location);
            picture.Invalidate();
        };
        picture.Paint += (_, e) =>
        {
            if (start != null && !drag.IsEmpty) e.Graphics.DrawRectangle(Pens.Red, drag);
        };
        picture.MouseUp += (_, e) =>
        {
            if (start is not Point from) return;
            start = null;
            if (Selection() is { } choice && picture.Image is { } image &&
                TryMask(picture.ClientSize, image.Size, FromPoints(from, e.Location), out var mask))
                Masks(choice).Add(mask);
            LoadPicture();
        };

        var top = Bar(DockStyle.Top);
        var endpoint = Box(270, "分析先URL", "https://api.openai.com/v1");
        var model = Box(160, "モデルID");
        var key = Box(150, "APIキー");
        key.UseSystemPasswordChar = true;
        top.Controls.AddRange([Label("分析先URL"), endpoint, Label("モデルID"), model, Label("APIキー"), key]);
        var bottom = Bar();
        var analyze = Button("送信内容を確認…");
        var open = Button("保存済みの案を開く…");
        var close = Button("閉じる");
        close.Click += (_, _) => form.Close();
        open.Click += (_, _) =>
        {
            using var file = new OpenFileDialog { Filter = "手順案 (*.json)|*.json" };
            if (file.ShowDialog(form) != DialogResult.OK) return;
            try { Edit(form, SkillDraftFiles.Read<SkillDraft>(file.FileName), directory); }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { Error(form, error); }
        };
        analyze.Click += async (_, _) =>
        {
            bool busy = false;
            try
            {
                grid.EndEdit();
                foreach (DataGridViewRow row in grid.Rows)
                {
                    var step = (SkillSelection)row.Tag;
                    step.Include = row.Cells[0].Value is true;
                    step.Action = Convert.ToString(row.Cells[3].Value);
                    step.Narration = Convert.ToString(row.Cells[4].Value);
                    step.IncludeBefore = row.Cells[5].Value is true;
                    step.IncludeAfter = row.Cells[6].Value is true;
                }
                var review = source.Prepare(grid.Rows.Cast<DataGridViewRow>().Select(row => (SkillSelection)row.Tag));
                var address = SkillAnalysis.Endpoint(endpoint.Text);
                string modelId = model.Text.Trim();
                if (modelId.Length == 0) throw new ArgumentException("モデルIDを指定してください。");
                if (!Review(form, review, "AIに送信する内容", address.AbsoluteUri + "　" + modelId)) return;
                busy = true;
                analyze.Enabled = open.Enabled = false;
                form.UseWaitCursor = true;
                var draft = await SkillAnalysis.Generate(review, address, modelId, key.Text, cancel.Token);
                if (!form.IsDisposed) Edit(form, draft, directory);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            catch (Exception error) { if (!form.IsDisposed) Error(form, error); }
            finally
            {
                if (busy && !form.IsDisposed)
                {
                    analyze.Enabled = open.Enabled = true;
                    form.UseWaitCursor = false;
                }
            }
        };
        bottom.Controls.AddRange([analyze, open, close]);
        form.Controls.Add(sides);
        form.Controls.Add(top);
        form.Controls.Add(bottom);
        LoadPicture();
        form.ShowDialog(owner);
        cancel.Cancel();
        picture.Image?.Dispose();
    }

    static Rectangle FromPoints(Point a, Point b) => Rectangle.FromLTRB(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    internal static bool TryMask(Size viewport, Size image, Rectangle drag, out SkillMask mask)
    {
        mask = null;
        double scale = Math.Min((double)viewport.Width / image.Width, (double)viewport.Height / image.Height);
        if (scale <= 0 || drag.Width < 2 || drag.Height < 2) return false;
        double x = (viewport.Width - image.Width * scale) / 2;
        double y = (viewport.Height - image.Height * scale) / 2;
        int left = Math.Clamp((int)Math.Floor((drag.Left - x) / scale), 0, image.Width);
        int top = Math.Clamp((int)Math.Floor((drag.Top - y) / scale), 0, image.Height);
        int right = Math.Clamp((int)Math.Ceiling((drag.Right - x) / scale), 0, image.Width);
        int bottom = Math.Clamp((int)Math.Ceiling((drag.Bottom - y) / scale), 0, image.Height);
        if (right <= left || bottom <= top) return false;
        mask = new SkillMask(left, top, right - left, bottom - top);
        return true;
    }

    static bool Review(Form owner, SkillReview review, string title, string destination = null)
    {
        using var form = Window(title, 810, 610);
        var head = Bar(DockStyle.Top);
        if (destination != null) head.Controls.Add(Label(destination));
        var list = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "確認する操作", Width = 160 };
        list.Items.AddRange(review.Steps.Select(step => (object)$"操作 {step.Seq}").ToArray());
        head.Controls.Add(list);
        var detail = new TextBox { Dock = DockStyle.Top, Height = 92, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical,
            AccessibleName = "送信する操作と文字起こし" };
        var images = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        images.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        images.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var before = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = "操作前画像" };
        var after = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = "操作後画像" };
        images.Controls.Add(before, 0, 0);
        images.Controls.Add(after, 1, 0);
        void SetImage(PictureBox box, SkillImage image)
        {
            box.Image?.Dispose();
            box.Image = null;
            if (image == null) return;
            using var stream = new MemoryStream(image.Png);
            using var original = Image.FromStream(stream);
            box.Image = new Bitmap(original);
        }
        list.SelectedIndexChanged += (_, _) =>
        {
            var step = review.Steps[list.SelectedIndex];
            detail.Text = $"{step.AtMs} ms\r\n{step.Action}\r\n{step.Narration}";
            SetImage(before, step.Images.FirstOrDefault(i => i.Side == "before"));
            SetImage(after, step.Images.FirstOrDefault(i => i.Side == "after"));
        };
        list.SelectedIndex = 0;
        var bottom = Bar();
        if (destination != null)
        {
            var send = Button("送信して分析");
            send.DialogResult = DialogResult.OK;
            bottom.Controls.Add(send);
        }
        var close = Button("閉じる");
        close.DialogResult = DialogResult.Cancel;
        bottom.Controls.Add(close);
        form.CancelButton = close;
        form.Controls.Add(images);
        form.Controls.Add(detail);
        form.Controls.Add(head);
        form.Controls.Add(bottom);
        bool accepted = form.ShowDialog(owner) == DialogResult.OK;
        before.Image?.Dispose();
        after.Image?.Dispose();
        return accepted;
    }

    static void Edit(Form owner, SkillDraft draft, string directory)
    {
        SkillDrafts.Validate(draft);
        using var form = Window("手順案を編集", 940, 690);
        var plan = draft.Procedure;
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), ColumnCount = 2 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        TextBox Field(string label, string value, int rows)
        {
            int index = fields.RowCount++;
            fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var input = Box(500, label, value);
            input.Dock = DockStyle.Top;
            input.Multiline = rows > 1;
            if (rows > 1)
            {
                input.Height = rows * 21;
                input.ScrollBars = ScrollBars.Vertical;
            }
            fields.Controls.Add(Label(label), 0, index);
            fields.Controls.Add(input, 1, index);
            return input;
        }
        var goal = Field("目的", plan.Goal, 1);
        var when = Field("使用条件", plan.UseWhen, 2);
        var prerequisites = Field("前提", plan.Prerequisites, 2);
        var parameters = Field("可変項目", plan.Parameters, 2);
        var completion = Field("完了条件", plan.Completion, 2);
        var grid = Grid();
        grid.RowTemplate.Height = 44;
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        var instruction = TextColumn("手順", 560);
        instruction.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        grid.Columns.AddRange(instruction, TextColumn("根拠の操作番号（カンマ区切り）", 240));
        foreach (var step in plan.Steps) grid.Rows.Add(step.Instruction, string.Join(", ", step.Evidence));
        var gridButtons = Bar(DockStyle.Top);
        var add = Button("手順を追加");
        var remove = Button("手順を削除");
        var up = Button("↑");
        var down = Button("↓");
        var evidence = Button("根拠を見る…");
        add.Click += (_, _) =>
        {
            int index = grid.Rows.Add("", "");
            grid.CurrentCell = grid.Rows[index].Cells[0];
            grid.BeginEdit(true);
        };
        remove.Click += (_, _) => { if (grid.CurrentRow != null) grid.Rows.Remove(grid.CurrentRow); };
        void MoveStep(int direction)
        {
            grid.EndEdit();
            int index = grid.CurrentRow?.Index ?? -1;
            int moved = index + direction;
            if (index < 0 || moved < 0 || moved >= grid.Rows.Count) return;
            var row = grid.Rows[index];
            var values = row.Cells.Cast<DataGridViewCell>().Select(cell => cell.Value).ToArray();
            grid.Rows.RemoveAt(index);
            grid.Rows.Insert(moved, values);
            grid.CurrentCell = grid.Rows[moved].Cells[0];
        }
        up.Click += (_, _) => MoveStep(-1);
        down.Click += (_, _) => MoveStep(1);
        evidence.Click += (_, _) =>
        {
            try
            {
                grid.EndEdit();
                if (grid.CurrentRow == null) return;
                var refs = EvidenceIds(grid.CurrentRow.Cells[1].Value);
                var selected = draft.Review.Steps.Where(step => refs.Contains(step.Seq)).ToList();
                if (selected.Count != refs.Count) throw new ArgumentException("分析時に選んだ操作番号を指定してください。");
                Review(form, new SkillReview(selected), "手順の根拠");
            }
            catch (Exception error) { Error(form, error); }
        };
        gridButtons.Controls.AddRange([add, remove, up, down, evidence]);
        var editor = new Panel { Dock = DockStyle.Fill };
        editor.Controls.Add(grid);
        editor.Controls.Add(gridButtons);

        SkillDraft ReadEditor()
        {
            grid.EndEdit();
            var steps = grid.Rows.Cast<DataGridViewRow>().Select(row => new SkillInstruction(
                Convert.ToString(row.Cells[0].Value).Trim(), EvidenceIds(row.Cells[1].Value))).ToList();
            var edited = new SkillDraft(draft.Review, new SkillProcedure(goal.Text.Trim(), when.Text.Trim(), prerequisites.Text.Trim(),
                parameters.Text.Trim(), completion.Text.Trim(), steps));
            SkillDrafts.Validate(edited);
            return edited;
        }

        var actions = Bar();
        var name = Box(165, "スキル名", "recorded-workflow");
        var save = Button("案をJSONで保存…");
        var export = Button("SKILL.mdを確認…");
        var manual = Button("手順書を書き出す…");
        var close = Button("閉じる");
        close.Click += (_, _) => form.Close();
        save.Click += (_, _) =>
        {
            try
            {
                var edited = ReadEditor();
                string path = PickSave(form, "手順案 (*.json)|*.json", "skill-draft.json", directory);
                if (path != null) SkillDraftFiles.WriteJson(path, edited);
            }
            catch (Exception error) { Error(form, error); }
        };
        export.Click += (_, _) =>
        {
            try { PreviewSkill(form, SkillDrafts.RenderSkill(ReadEditor(), name.Text.Trim()), directory); }
            catch (Exception error) { Error(form, error); }
        };
        manual.Click += (_, _) =>
        {
            try
            {
                string markdown = SkillDrafts.RenderManual(ReadEditor());
                string path = PickSave(form, "Markdown (*.md)|*.md|HTML (*.html)|*.html", "分析済み手順書", directory);
                if (path != null)
                    SkillDraftFiles.WriteNew(path, Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase) ?
                        HtmlExporter.ToHtml(markdown) : markdown);
            }
            catch (Exception error) { Error(form, error); }
        };
        actions.Controls.AddRange([save, Label("スキル名"), name, export, manual, close]);
        form.Controls.Add(editor);
        form.Controls.Add(fields);
        form.Controls.Add(actions);
        form.ShowDialog(owner);
    }

    static List<long> EvidenceIds(object cell)
    {
        var ids = new List<long>();
        foreach (string token in Convert.ToString(cell).Replace('、', ',').Split(',', StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(token, out var id) || id <= 0)
                throw new ArgumentException("根拠の操作番号をカンマ区切りで入力してください。");
            if (!ids.Contains(id)) ids.Add(id);
        }
        return ids;
    }

    static void PreviewSkill(Form owner, string text, string directory)
    {
        using var preview = Window("SKILL.mdの確認", 780, 590);
        var content = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both,
            WordWrap = false, Text = text.ReplaceLineEndings(Environment.NewLine), AccessibleName = "SKILL.mdの内容" };
        var bar = Bar();
        var save = Button("SKILL.mdを書き出す…");
        var close = Button("閉じる");
        close.Click += (_, _) => preview.Close();
        save.Click += (_, _) =>
        {
            try
            {
                string path = PickSave(preview, "Markdown (*.md)|*.md", "SKILL.md", directory);
                if (path == null) return;
                if (!Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("ファイル名は SKILL.md を指定してください。");
                SkillDraftFiles.WriteNew(path, text);
                preview.Close();
            }
            catch (Exception error) { Error(preview, error); }
        };
        bar.Controls.AddRange([save, close]);
        preview.Controls.Add(content);
        preview.Controls.Add(bar);
        preview.ShowDialog(owner);
    }

    static string PickSave(Form owner, string filter, string name, string directory)
    {
        using var file = new SaveFileDialog { Filter = filter, FileName = name, AddExtension = true, OverwritePrompt = false };
        if (file.ShowDialog(owner) != DialogResult.OK) return null;
        SkillDraftFiles.OutsideRecording(file.FileName, directory);
        return file.FileName;
    }
}
