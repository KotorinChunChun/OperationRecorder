using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using OpRec;

namespace OperationRecorderApp
{
    // 2 つの記録を合成する画面(記録一覧の「ほかの記録と合成…」から開く)。
    // 突き合わせとまとめ方の案は SessionMerger.Plan、合成は SessionMerger.Merge にあり、ここは受け渡しだけ
    public sealed class MergeForm : Form
    {
        readonly string dirA;
        readonly ComboBox cmbB = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 520 };
        readonly RadioButton rbPreferB = new RadioButton { Text = "取り直した記録(B)を採る", AutoSize = true, Checked = true };
        readonly RadioButton rbPreferA = new RadioButton { Text = "前の記録(A)を採る", AutoSize = true };
        readonly ListView lv = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false };
        readonly Label lblSummary = new Label { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        List<JsonObject> a = new List<JsonObject>(), b = new List<JsonObject>();
        List<SessionMerger.Row> plan = new List<SessionMerger.Row>();
        bool filling;

        public string MergedDir { get; private set; }   // 作った記録フォルダ(作らなかったら null)

        sealed class SessionChoice { public string Dir, Text; public override string ToString() => Text; }

        public MergeForm(string sessionDirA, IEnumerable<(string Dir, string Text)> others)
        {
            dirA = sessionDirA;
            Text = WindowTitle.Create("記録の合成");
            Font = new Font("Yu Gothic UI", 9.5f);
            Size = new Size(1100, 760);
            MinimumSize = new Size(800, 500);
            StartPosition = FormStartPosition.CenterParent;
            ShowIcon = false;

            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8) };
            top.Controls.Add(new Label { Text = "前の記録(A)", AutoSize = true, Margin = new Padding(3, 6, 12, 3) }, 0, 0);
            top.Controls.Add(new Label { Text = Path.GetFileName(dirA), AutoSize = true, Margin = new Padding(3, 6, 3, 3) }, 1, 0);
            top.Controls.Add(new Label { Text = "取り直した記録(B)", AutoSize = true, Margin = new Padding(3, 6, 12, 3) }, 0, 1);
            top.Controls.Add(cmbB, 1, 1);
            top.Controls.Add(new Label { Text = "両方にある手順は", AutoSize = true, Margin = new Padding(3, 6, 12, 3) }, 0, 2);
            var prefer = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            prefer.Controls.AddRange(new Control[] { rbPreferB, rbPreferA });
            top.Controls.Add(prefer, 1, 2);

            lv.Columns.Add("入れる", 60);
            lv.Columns.Add("どちらの記録", 110);
            lv.Columns.Add("手順", 640);
            lv.Columns.Add("突き合わせ", 200);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
            var btnMerge = new Button { Text = "合成して新しい記録を作る", AutoSize = true, Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold) };
            var btnCancel = new Button { Text = "閉じる", AutoSize = true, DialogResult = DialogResult.Cancel };
            bottom.Controls.AddRange(new Control[] { btnMerge, btnCancel, lblSummary });

            Controls.Add(lv);
            Controls.Add(top);
            Controls.Add(bottom);
            CancelButton = btnCancel;

            foreach (var o in others) cmbB.Items.Add(new SessionChoice { Dir = o.Dir, Text = o.Text });
            cmbB.SelectedIndexChanged += (s, e) => LoadB();
            rbPreferA.CheckedChanged += (s, e) => { if (!filling) Rebuild(); };
            // 一覧が初めて表示されるときにも、チェックの状態を写すために呼ばれる(そのときは項目を作っている途中なので、全体は見ない)
            lv.ItemChecked += (s, e) =>
            {
                if (filling || !(e.Item?.Tag is SessionMerger.Row r)) return;
                r.Include = e.Item.Checked;
                e.Item.ForeColor = r.Include ? SystemColors.WindowText : Color.Gray;
                ShowSummary();
            };
            // 両方にある手順は、ダブルクリックで A / B を切り替える
            lv.DoubleClick += (s, e) =>
            {
                if (lv.SelectedItems.Count == 0 || !(lv.SelectedItems[0].Tag is SessionMerger.Row r) || r.Kind != SessionMerger.RowKind.Both) return;
                r.UseA = !r.UseA;
                Fill();
            };
            btnMerge.Click += (s, e) => DoMerge();

            a = SessionMerger.LoadEvents(dirA);
            if (cmbB.Items.Count > 0) cmbB.SelectedIndex = 0;
        }

        void LoadB()
        {
            var c = cmbB.SelectedItem as SessionChoice;
            b = c == null ? new List<JsonObject>() : SessionMerger.LoadEvents(c.Dir);
            Rebuild();
        }

        void Rebuild()
        {
            plan = SessionMerger.Plan(a, b, rbPreferA.Checked);
            Fill();
        }

        void Fill()
        {
            filling = true;
            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (var r in plan)
            {
                bool fromA = r.Kind == SessionMerger.RowKind.OnlyA || (r.Kind == SessionMerger.RowKind.Both && r.UseA);
                var e = fromA ? a[r.A] : b[r.B];
                string head = ReportGenerator.HeadlineFromJson(e.ToJsonString());
                if (string.IsNullOrEmpty(head)) head = "(画面キャプチャ)";
                string which = r.Kind == SessionMerger.RowKind.Both ? (r.UseA ? "A(両方にある)" : "B(両方にある)") : r.Kind == SessionMerger.RowKind.OnlyA ? "A だけ" : "B だけ";
                string match = r.Kind == SessionMerger.RowKind.Both ? "A " + (r.A + 1) + " = B " + (r.B + 1) + "(ダブルクリックで切替)"
                             : r.Kind == SessionMerger.RowKind.OnlyA ? "A " + (r.A + 1) : "B " + (r.B + 1);
                var item = new ListViewItem(new[] { "", which, head, match }) { Checked = r.Include, Tag = r };
                if (!r.Include) item.ForeColor = Color.Gray;
                lv.Items.Add(item);
            }
            lv.EndUpdate();
            filling = false;
            ShowSummary();
        }

        void ShowSummary()
        {
            int n = plan.Count(r => r.Include);
            lblSummary.Text = "合成すると " + n + " 手順になります(A " + a.Count + " 手順 / B " + b.Count + " 手順、両方にある手順 " + plan.Count(r => r.Kind == SessionMerger.RowKind.Both) + ")";
        }

        void DoMerge()
        {
            if (!(cmbB.SelectedItem is SessionChoice)) return;
            if (!plan.Any(r => r.Include)) return;
            try
            {
                Cursor = Cursors.WaitCursor;
                MergedDir = SessionMerger.Merge(dirA, ((SessionChoice)cmbB.SelectedItem).Dir, plan, Path.GetDirectoryName(dirA.TrimEnd('\\', '/')));
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex) { MessageBox.Show(this, "合成できませんでした: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { Cursor = Cursors.Default; }
        }
    }
}
