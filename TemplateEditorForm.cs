using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace OperationRecorderApp
{
    // テンプレートのエディタ(記録一覧の「テンプレートを編集…」から開く)。
    // 左 = テンプレートの一覧と追加・削除 / 中 = 中身の編集と保存 / 右 = 選んでいる記録に当てはめたプレビュー。
    // 追加・削除・保存・誤りの確認・プレビューの中身は TemplateStore にあり、ここは受け渡しだけ
    public sealed class TemplateEditorForm : Form
    {
        readonly string dir, sessionDir;
        readonly ListBox lbTemplates = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        readonly TextBox txtNewName = new TextBox { Width = 200, PlaceholderText = "新しいテンプレートの名前" };
        readonly TextBox txtBody = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, AcceptsTab = true, AcceptsReturn = true,
            Font = new Font("BIZ UDGothic", 10f),
        };
        readonly Label lblPath = new Label { Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(0, 0, 0, 4) };
        readonly Label lblResult = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 3), MaximumSize = new Size(560, 0) };
        readonly WebBrowser preview = new WebBrowser { Dock = DockStyle.Fill, ScriptErrorsSuppressed = true, AllowWebBrowserDrop = false };
        readonly Label lblPreviewNote = new Label { Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(0, 0, 0, 4) };
        string current;          // 開いているテンプレートの名前
        bool dirty, loading;

        public TemplateEditorForm(string templateDir, string sessionDir)
        {
            dir = templateDir; this.sessionDir = sessionDir;
            Text = WindowTitle.Create("テンプレートのエディタ");
            Font = new Font("Yu Gothic UI", 9.5f);
            Size = new Size(1400, 860);
            MinimumSize = new Size(1000, 600);
            StartPosition = FormStartPosition.CenterParent;
            ShowIcon = false;

            // 左: 一覧と追加・削除
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.Controls.Add(new Label { Text = "テンプレート", AutoSize = true, Font = new Font("Yu Gothic UI", 9.5f, FontStyle.Bold) }, 0, 0);
            left.Controls.Add(lbTemplates, 0, 1);
            var btnCreate = new Button { Text = "今のテンプレートをもとに追加", AutoSize = true };
            var btnDelete = new Button { Text = "このテンプレートを削除", AutoSize = true, ForeColor = Color.DarkRed };
            var leftBtns = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            leftBtns.Controls.AddRange(new Control[] { txtNewName, btnCreate, btnDelete });
            left.Controls.Add(leftBtns, 0, 2);

            // 中: 編集
            var mid = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };
            var btnSave = new Button { Text = "保存", AutoSize = true, Font = new Font("Yu Gothic UI", 9.5f, FontStyle.Bold) };
            var btnRevert = new Button { Text = "保存した内容に戻す", AutoSize = true };
            var btnPreview = new Button { Text = "プレビューを更新", AutoSize = true };
            var midBtns = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true };
            midBtns.Controls.AddRange(new Control[] { btnSave, btnRevert, btnPreview, lblResult });
            mid.Controls.Add(txtBody);
            mid.Controls.Add(midBtns);
            mid.Controls.Add(lblPath);

            // 右: プレビュー
            var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };
            right.Controls.Add(preview);
            right.Controls.Add(lblPreviewNote);
            lblPreviewNote.Text = sessionDir != null
                ? "プレビュー: " + Path.GetFileName(sessionDir) + "(保存していない内容でも表示できます)"
                : "プレビュー: 記録一覧で記録を選んでから開くと、その記録に当てはめて表示します";

            var splitRight = new SplitContainer { Dock = DockStyle.Fill };
            splitRight.Panel1.Controls.Add(mid);
            splitRight.Panel2.Controls.Add(right);
            var splitLeft = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            splitLeft.Panel1.Controls.Add(left);
            splitLeft.Panel2.Controls.Add(splitRight);
            Controls.Add(splitLeft);
            Shown += (s, e) => { splitLeft.SplitterDistance = 260; splitRight.SplitterDistance = Math.Max(300, splitRight.Width / 2); };

            lbTemplates.SelectedIndexChanged += (s, e) => { if (!loading) OpenSelected(); };
            txtBody.TextChanged += (s, e) => { if (!loading) { dirty = true; ShowResult(null, false); } };
            btnSave.Click += (s, e) => SaveCurrent();
            btnRevert.Click += (s, e) => { if (current != null) { dirty = false; Open(current); } };
            btnPreview.Click += (s, e) => UpdatePreview();
            btnCreate.Click += (s, e) => CreateNew();
            btnDelete.Click += (s, e) => DeleteCurrent();
            FormClosing += (s, e) => { if (!ConfirmLeave()) e.Cancel = true; };

            ReloadList(null);
        }

        void ReloadList(string select)
        {
            loading = true;
            lbTemplates.Items.Clear();
            foreach (var t in OutputChoices.LoadTemplates(dir)) lbTemplates.Items.Add(t);
            loading = false;
            var item = lbTemplates.Items.Cast<OutputChoices.TemplateItem>().FirstOrDefault(t => t.Name == (select ?? current)) ?? lbTemplates.Items.Cast<OutputChoices.TemplateItem>().FirstOrDefault();
            if (item != null) lbTemplates.SelectedItem = item;
            else { current = null; loading = true; txtBody.Text = ""; loading = false; lblPath.Text = dir; }
        }

        void OpenSelected()
        {
            var t = lbTemplates.SelectedItem as OutputChoices.TemplateItem;
            if (t == null || t.Name == current) return;
            if (!ConfirmLeave()) { loading = true; lbTemplates.SelectedItem = lbTemplates.Items.Cast<OutputChoices.TemplateItem>().FirstOrDefault(i => i.Name == current); loading = false; return; }
            Open(t.Name);
        }

        void Open(string name)
        {
            current = name;
            loading = true;
            txtBody.Text = TemplateStore.Read(dir, name).Replace("\r\n", "\n").Replace("\n", "\r\n");
            loading = false;
            dirty = false;
            lblPath.Text = TemplateStore.PathOf(dir, name);
            ShowResult(null, false);
            UpdatePreview();
        }

        // 保存していない変更があれば、保存するか聞く。続けてよければ true
        bool ConfirmLeave()
        {
            if (!dirty || current == null) return true;
            var r = MessageBox.Show(this, "「" + current + "」の変更を保存しますか?", Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) return SaveCurrent();
            dirty = false;
            return true;
        }

        string BodyText => txtBody.Text.Replace("\r\n", "\n");

        bool SaveCurrent()
        {
            if (current == null) return true;
            try
            {
                TemplateStore.Save(dir, current, BodyText);
                dirty = false;
                ShowResult("保存しました: " + TemplateStore.PathOf(dir, current), false);
                UpdatePreview();
                return true;
            }
            catch (Exception ex) { ShowResult("保存できません: " + ex.Message, true); return false; }
        }

        void CreateNew()
        {
            string name = txtNewName.Text.Trim();
            string err = TemplateStore.CheckNewName(dir, name);
            if (err != null) { ShowResult(err, true); return; }
            if (!ConfirmLeave()) return;
            try
            {
                TemplateStore.Create(dir, name, current != null ? BodyText : null);
                txtNewName.Text = "";
                ReloadList(name);
                ShowResult("「" + name + "」を追加しました。冒頭コメントの 3 行目が、一覧に出る説明になります。", false);
            }
            catch (Exception ex) { ShowResult(ex.Message, true); }
        }

        void DeleteCurrent()
        {
            if (current == null) return;
            if (MessageBox.Show(this, "テンプレート「" + current + "」をごみ箱へ移します。よろしいですか?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                TemplateStore.Delete(dir, current, true);
                dirty = false;
                current = null;
                ReloadList(null);
            }
            catch (Exception ex) { ShowResult("削除できません: " + ex.Message, true); }
        }

        void UpdatePreview()
        {
            if (sessionDir == null) { preview.DocumentText = "<html><body></body></html>"; return; }
            try
            {
                Cursor = Cursors.WaitCursor;
                string md = TemplateStore.Preview(sessionDir, BodyText);
                string html = Path.Combine(Path.GetTempPath(), "OperationRecorder-template-preview", "preview.html");
                File.WriteAllText(html, HtmlExporter.ToHtml(md), new UTF8Encoding(false));
                preview.Navigate(html);
            }
            catch (Exception ex)
            {
                ShowResult("プレビューできません: " + ex.Message, true);
            }
            finally { Cursor = Cursors.Default; }
        }

        void ShowResult(string text, bool error)
        {
            lblResult.ForeColor = error ? Color.Firebrick : Color.DarkGreen;
            lblResult.Text = text ?? (dirty ? "(保存していない変更があります)" : "");
            if (text == null && dirty) lblResult.ForeColor = Color.DarkOrange;
        }
    }
}
