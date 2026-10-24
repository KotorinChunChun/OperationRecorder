using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using OpRec;

namespace OperationRecorderApp
{
    // 元の記録を読むだけにし、完成順と採用元は独立した合成作業へ保存する。
    public sealed partial class MergeForm : Form
    {
        readonly CompositionProject project;
        readonly string outputRoot;
        readonly Action<Exception> errorReporter;
        readonly Stack<string> undo = new Stack<string>(), redo = new Stack<string>();
        string savedState, editorRowId;
        bool filling, busy, finished, editorDirty, disposing, refreshingNumbers, numberRefreshQueued;

        readonly ComboBox sources = Combo("compositionSources");
        readonly CheckBox suggest = new CheckBox { Name = "compositionSuggest", Text = "重複候補を推定して追加", Checked = true, AutoSize = true };
        readonly ListView sourceSteps = List("compositionSourceSteps", false);
        readonly ListView rows = List("compositionRows", true);
        readonly TextBox caption = new TextBox { Name = "compositionCaption", Dock = DockStyle.Fill };
        readonly TextBox description = new TextBox { Name = "compositionDescription", Dock = DockStyle.Fill };
        readonly TextBox sourcePath = new TextBox { Name = "compositionSourcePath", ReadOnly = true, Dock = DockStyle.Fill };
        readonly ComboBox candidate = Combo("compositionCandidate"), comparison = Combo("compositionComparison");
        readonly TextBox headline = new TextBox { Name = "compositionHeadline", Dock = DockStyle.Fill };
        readonly TextBox body = new TextBox { Name = "compositionBody", Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        readonly Label summary = new Label { Name = "compositionSummary", AutoSize = true, Padding = new Padding(3, 6, 3, 3) };
        readonly Label saveState = new Label { Name = "compositionSaveState", AutoSize = true, Padding = new Padding(3, 6, 3, 3) };
        readonly Label draftPath = new Label { Name = "compositionDraftPath", AutoSize = true, MaximumSize = new Size(1250, 0), ForeColor = SystemColors.GrayText };
        readonly Label selectedText = new Label { Name = "compositionSelectedText", Dock = DockStyle.Fill, AutoEllipsis = true };
        readonly TextBox comparedText = new TextBox { Name = "compositionComparedText", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        readonly PictureBox before = Picture("compositionBefore"), after = Picture("compositionAfter");
        readonly PictureBox compareBefore = Picture("compositionCompareBefore"), compareAfter = Picture("compositionCompareAfter");
        readonly PictureBox chosenBefore = Picture("compositionChosenBefore"), chosenAfter = Picture("compositionChosenAfter");
        readonly ToolTip tips = new ToolTip();
        readonly List<Control> mutating = new List<Control>();
        Button undoButton, redoButton, finishButton;

        public string MergedDir { get; private set; }
        public string DraftFile => project.DraftFile;
        public bool HasUnsavedChanges => editorDirty || project.CaptureState() != savedState;

        // 旧2記録の入口を維持する。未編集で閉じるだけなら保存確認を出さない。
        public MergeForm(string sessionDirA, IEnumerable<(string Dir, string Text)> others)
            : this(new[] { sessionDirA }.Concat((others ?? Enumerable.Empty<(string Dir, string Text)>()).Take(1).Select(item => item.Dir)),
                Path.GetDirectoryName(sessionDirA.TrimEnd('\\', '/'))) { }

        public MergeForm(IEnumerable<string> sessionDirectories, string outputRoot, Action<Exception> errorReporter = null)
            : this(CompositionProject.Create(sessionDirectories, Path.Combine(Path.GetFullPath(outputRoot), ".compositions")), outputRoot, errorReporter) { }

        public static MergeForm Resume(string draftFile, string outputRoot, Action<Exception> errorReporter = null) => new MergeForm(CompositionProject.Open(draftFile), outputRoot, errorReporter);

        MergeForm(CompositionProject project, string outputRoot, Action<Exception> errorReporter = null)
        {
            this.project = project;
            this.outputRoot = Path.GetFullPath(outputRoot);
            this.errorReporter = errorReporter;
            Text = WindowTitle.Create("記録の合成");
            Icon = (Icon)AppIcons.Idle.Clone();
            Font = new Font("Yu Gothic UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Dpi;
            MinimumSize = new Size(1100, 720);
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Size = new Size(Math.Min(1500, area.Width), Math.Min(920, area.Height));
            StartPosition = FormStartPosition.CenterParent;
            BuildLayout();
            WireEvents();
            savedState = project.CaptureState();
            FillAll();
        }

        static ComboBox Combo(string name) => new ComboBox { Name = name, DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        static ListView List(string name, bool check) => new CompositionListView
        {
            Name = name, Dock = DockStyle.Fill, View = View.Details, CheckBoxes = check,
            MultiSelect = true, FullRowSelect = true, HideSelection = false, ShowItemToolTips = true
        };
        static PictureBox Picture(string name) => new PictureBox
        {
            Name = name, Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(235, 235, 235)
        };
        // ハンドル生成・再生成のチェック同期は利用者の採否変更として扱わない。
        // ListView内部の項目対応表が未完成な間にはItemCheckedも発火する。
        sealed class CompositionListView : ListView
        {
            public bool SynchronizingNativeState { get; private set; }
            protected override void OnHandleCreated(EventArgs e)
            {
                bool previous = SynchronizingNativeState;
                SynchronizingNativeState = true;
                try { base.OnHandleCreated(e); }
                finally { SynchronizingNativeState = previous; }
            }
            protected override void DestroyHandle()
            {
                bool previous = SynchronizingNativeState;
                SynchronizingNativeState = true;
                try { base.DestroyHandle(); }
                finally { SynchronizingNativeState = previous; }
            }
            protected override void OnHandleDestroyed(EventArgs e)
            {
                bool previous = SynchronizingNativeState;
                SynchronizingNativeState = true;
                try { base.OnHandleDestroyed(e); }
                finally { SynchronizingNativeState = previous; }
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) SynchronizingNativeState = true;
                base.Dispose(disposing);
            }
        }
        sealed class Choice
        {
            public string Id, Text;
            public int Index;
            public override string ToString() => Text;
        }
        CompositionRow SelectedRow => rows.SelectedItems.Count == 1 ? project.Rows.FirstOrDefault(row => row.Id == (string)rows.SelectedItems[0].Tag) : null;
        string SelectedSourceId => (sources.SelectedItem as Choice)?.Id;
        string[] SelectedRowIds() => rows.SelectedItems.Cast<ListViewItem>().OrderBy(item => item.Index).Select(item => (string)item.Tag).ToArray();

        void WireEvents()
        {
            sources.SelectedIndexChanged += (s, e) => { if (!filling) FillSourceSteps(); };
            rows.SelectedIndexChanged += (s, e) =>
            {
                if (filling) return;
                CommitEditors();
                ShowEditor();
            };
            rows.ItemChecked += (s, e) =>
            {
                if (filling || busy || disposing || refreshingNumbers ||
                    ((CompositionListView)rows).SynchronizingNativeState || !(e.Item?.Tag is string id)) return;
                var row = project.Rows.FirstOrDefault(item => item.Id == id);
                if (row == null || row.Included == e.Item.Checked) return;
                Remember();
                row.Included = e.Item.Checked;
                e.Item.ForeColor = row.Included ? SystemColors.WindowText : Color.Gray;
                QueueNumbers(); UpdateStatus();
            };
            candidate.SelectedIndexChanged += (s, e) =>
            {
                if (filling || busy || SelectedRow == null || !(candidate.SelectedItem is Choice choice)) return;
                var row = SelectedRow;
                if (row.SelectedCandidate != choice.Index) Change(() => project.Choose(row.Id, choice.Index));
            };
            comparison.SelectedIndexChanged += (s, e) => { if (!filling) ShowComparison(); };
            headline.TextChanged += (s, e) => MarkEditorDirty();
            body.TextChanged += (s, e) => MarkEditorDirty();
            caption.TextChanged += (s, e) => MarkEditorDirty();
            description.TextChanged += (s, e) => MarkEditorDirty();
            FormClosing += ConfirmClose;
            foreach (var picture in new[] { before, after, compareBefore, compareAfter, chosenBefore, chosenAfter })
            {
                tips.SetToolTip(picture, "ダブルクリックすると画像を大きく表示します。");
                picture.DoubleClick += (s, e) => OpenImage((PictureBox)s);
            }
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.S) { SaveDraft(); e.SuppressKeyPress = true; }
                else if (e.Control && e.KeyCode == Keys.Z && !(ActiveControl is TextBoxBase)) { Undo(); e.SuppressKeyPress = true; }
                else if (e.Control && e.KeyCode == Keys.Y && !(ActiveControl is TextBoxBase)) { Redo(); e.SuppressKeyPress = true; }
            };
        }

        void MarkEditorDirty()
        {
            if (filling) return;
            editorDirty = true;
            UpdateStatus();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.disposing = true;
                foreach (var picture in new[] { before, after, compareBefore, compareAfter, chosenBefore, chosenAfter }) SetImage(picture, null);
                tips.Dispose();
                Icon?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}