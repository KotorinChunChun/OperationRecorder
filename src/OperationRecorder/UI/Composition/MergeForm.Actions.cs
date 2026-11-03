using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using OpRec;

namespace OperationRecorderApp
{
    public sealed partial class MergeForm
    {
        void FillAll(string[] selected = null)
        {
            selected = selected ?? SelectedRowIds();
            string sourceId = SelectedSourceId;
            filling = true;
            try
            {
                caption.Text = project.Caption ?? ""; description.Text = project.Description ?? "";
                sources.Items.Clear();
                for (int i = 0; i < project.Sources.Count; i++)
                {
                    var source = project.Sources[i];
                    sources.Items.Add(new Choice { Id = source.Id, Text = "記録 " + (i + 1) + ": " + source.Caption });
                }
                sources.SelectedIndex = project.Sources.FindIndex(item => item.Id == sourceId);
                if (sources.SelectedIndex < 0 && sources.Items.Count > 0) sources.SelectedIndex = 0;
                rows.BeginUpdate(); rows.Items.Clear();
                int number = 0;
                foreach (var row in project.Rows)
                {
                    var step = project.GetStep(row);
                    string origin = row.Candidates.Count == 0 ? "説明手順" : ReferenceText(row.Candidates[row.SelectedCandidate]);
                    if (row.Candidates.Count > 1) origin += " / 候補 " + row.Candidates.Count;
                    var item = new ListViewItem(new[] { row.Included ? (++number).ToString() : "—", step.Headline ?? "(説明手順)", origin })
                    {
                        Tag = row.Id, Checked = row.Included, ToolTipText = origin + "\n" + step.Headline,
                        ForeColor = row.Included ? SystemColors.WindowText : Color.Gray
                    };
                    rows.Items.Add(item);
                    item.Selected = selected.Contains(row.Id);
                }
                if (rows.SelectedItems.Count == 0 && rows.Items.Count > 0) rows.Items[0].Selected = true;
                rows.EndUpdate();
            }
            finally { filling = false; }
            editorDirty = false;
            editing.SelectRows(SelectedRowIds());
            FillSourceSteps(); ShowEditor(); UpdateStatus();
        }

        string ReferenceText(CompositionReference reference)
        {
            int index = project.Sources.FindIndex(item => item.Id == reference.SourceId);
            var step = project.GetStep(reference);
            string sourceCaption = index < 0 ? "不明" : project.Sources[index].Caption;
            return "記録 " + (index + 1) + ": " + sourceCaption + " / 元手順 " + step.Number;
        }

        void FillSourceSteps()
        {
            var selected = sourceSteps.SelectedItems.Cast<ListViewItem>().Select(item => (long)item.Tag).ToArray();
            string id = SelectedSourceId;
            bool previousFilling = filling; filling = true;
            sourceSteps.BeginUpdate();
            try
            {
                sourceSteps.Items.Clear();
                sourcePath.Text = project.Sources.FirstOrDefault(item => item.Id == id)?.OriginalDirectory ?? "";
                if (id != null)
                    foreach (var step in project.SourceSteps(id))
                    {
                        var item = new ListViewItem(new[] { step.Number.ToString(), step.Headline ?? "(画面キャプチャ)" }) { Tag = step.Seq, ToolTipText = step.Headline };
                        sourceSteps.Items.Add(item);
                        item.Selected = selected.Contains(step.Seq);
                    }
            }
            finally { sourceSteps.EndUpdate(); filling = previousFilling; }
            SyncSourceSelection();
        }

        void SyncSourceSelection()
        {
            if (SelectedSourceId != null)
                editing.SelectSourceSteps(SelectedSourceId, sourceSteps.SelectedItems.Cast<ListViewItem>().OrderBy(item => item.Index).Select(item => (long)item.Tag));
        }

        void ShowEditor()
        {
            filling = true;
            try
            {
                var row = SelectedRow;
                editorRowId = row?.Id;
                candidate.Items.Clear(); comparison.Items.Clear();
                headline.Enabled = body.Enabled = row != null;
                headline.Text = body.Text = "";
                selectedText.Text = row == null ? "文章・画像の確認は完成順の手順を1つ選択してください。" : "";
                if (row != null)
                {
                    for (int i = 0; i < row.Candidates.Count; i++)
                    {
                        string text = ReferenceText(row.Candidates[i]);
                        candidate.Items.Add(new Choice { Index = i, Text = text });
                        comparison.Items.Add(new Choice { Index = i, Text = "比較: " + text });
                    }
                    candidate.SelectedIndex = row.Candidates.Count == 0 ? -1 : row.SelectedCandidate;
                    candidate.Enabled = row.Candidates.Count > 1;
                    comparison.Enabled = row.Candidates.Count > 1;
                    if (row.Candidates.Count > 0) comparison.SelectedIndex = row.Candidates.Count > 1 ? (row.SelectedCandidate + 1) % row.Candidates.Count : 0;
                    var step = project.GetStep(row);
                    headline.Text = step.Headline ?? ""; body.Text = step.Body ?? "";
                    selectedText.Text = row.Candidates.Count == 0 ? "説明のみの手順です。" : ReferenceText(row.Candidates[row.SelectedCandidate]);
                    LoadImage(before, step.BeforePath); LoadImage(after, step.AfterPath);
                    LoadImage(chosenBefore, step.BeforePath); LoadImage(chosenAfter, step.AfterPath);
                }
                else
                {
                    candidate.Enabled = comparison.Enabled = false;
                    foreach (var picture in new[] { before, after, chosenBefore, chosenAfter }) SetImage(picture, null);
                }
            }
            finally { filling = false; }
            editorDirty = false;
            ShowComparison(); UpdateStatus();
        }

        void ShowComparison()
        {
            var row = SelectedRow;
            if (row != null && comparison.SelectedItem is Choice choice)
            {
                var step = project.GetStep(row.Candidates[choice.Index]);
                LoadImage(compareBefore, step.BeforePath); LoadImage(compareAfter, step.AfterPath);
                comparedText.Text = (step.Headline ?? "") + Environment.NewLine + (step.Body ?? "");
            }
            else
            {
                SetImage(compareBefore, null); SetImage(compareAfter, null); comparedText.Text = "比較する重複候補はありません。";
            }
        }

        static void SetImage(PictureBox box, Image image)
        {
            var previous = box.Image; box.Image = image; box.Tag = null; previous?.Dispose();
        }

        void LoadImage(PictureBox box, string file)
        {
            try
            {
                if (string.IsNullOrEmpty(file)) { SetImage(box, null); tips.SetToolTip(box, "この手順に画像はありません。"); return; }
                SetImage(box, LoadScaledImage(file, 1280));
                box.Tag = file;
                tips.SetToolTip(box, "ダブルクリックで拡大: " + Path.GetFileName(file));
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is OutOfMemoryException || ex is UnauthorizedAccessException)
            {
                SetImage(box, null); tips.SetToolTip(box, "画像を開けません: " + ex.Message);
                selectedText.Text = "画像を開けません。完成前に保存元の画像を確認してください。";
            }
        }

        static Bitmap LoadScaledImage(string file, int maximumEdge)
        {
            // 通常の比較枠は表示サイズに縮小し、巨大な元画像を各枠に複製して保持しない。
            using (var image = Image.FromFile(file))
            {
                double ratio = Math.Min(1.0, (double)maximumEdge / Math.Max(image.Width, image.Height));
                int width = Math.Max(1, (int)Math.Round(image.Width * ratio));
                int height = Math.Max(1, (int)Math.Round(image.Height * ratio));
                var copy = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (var graphics = Graphics.FromImage(copy))
                {
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(image, 0, 0, width, height);
                }
                return copy;
            }
        }

        void OpenImage(PictureBox box)
        {
            if (!(box.Tag is string file)) return;
            Run(() =>
            {
                // 拡大時だけ元画像を読み直す。閉じた後はファイルも画像メモリーも保持しない。
                using (var image = LoadScaledImage(file, 4096))
                using (var form = new Form { Text = WindowTitle.Create("合成手順の画像"), Icon = (Icon)AppIcons.Idle.Clone(), Size = new Size(1000, 720), StartPosition = FormStartPosition.CenterParent })
                using (var picture = new PictureBox { Dock = DockStyle.Fill, Image = image, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(235, 235, 235) })
                {
                    form.Controls.Add(picture); form.ShowDialog(this); form.Icon?.Dispose();
                }
            });
        }
        void CommitEditors()
        {
            if (filling || !editorDirty) return;
            editing.StageText(editorRowId, caption.Text, description.Text, headline.Text, body.Text);
            editing.CommitPending();
            editorDirty = false;
        }

        void ApplyText() => Run(() => { CommitEditors(); FillAll(); });

        void Change(Action action, string[] select = null)
        {
            Run(() =>
            {
                CommitEditors();
                action();
                FillAll(select);
            });
        }

        void Run(Action action)
        {
            if (busy) return;
            busy = true; UseWaitCursor = true;
            foreach (var control in mutating) control.Enabled = false;
            try { action(); }
            catch (Exception ex)
            {
                if (errorReporter != null) errorReporter(ex);
                else interaction.Message(this, ex.Message, WindowTitle.Create("合成作業を続けられません"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                busy = false; UseWaitCursor = false;
                foreach (var control in mutating) control.Enabled = true;
                UpdateStatus();
            }
        }

        void QueueNumbers()
        {
            // ネイティブ通知の処理中にItemsを再列挙せず、対応表の安定後に番号を反映する。
            if (numberRefreshQueued || disposing || !IsHandleCreated) return;
            numberRefreshQueued = true;
            BeginInvoke(new Action(() =>
            {
                numberRefreshQueued = false;
                if (!disposing && !IsDisposed && !rows.IsDisposed) RefreshNumbers();
            }));
        }

        void RefreshNumbers()
        {
            refreshingNumbers = true;
            try
            {
                int number = 0;
                for (int i = 0; i < project.Rows.Count && i < rows.Items.Count; i++)
                {
                    var row = project.Rows[i];
                    string text = row.Included ? (++number).ToString() : "—";
                    var item = rows.Items[i];
                    if (item != null && (string)item.Tag == row.Id) item.Text = text;
                }
            }
            finally { refreshingNumbers = false; }
        }
        void UpdateStatus()
        {
            int count = project.Rows.Count(row => row.Included);
            summary.Text = project.Sources.Count + " 記録 / 完成 " + count + " 手順 / 除外 " + (project.Rows.Count - count);
            bool dirty = HasUnsavedChanges;
            saveState.Text = dirty ? "● 未保存の変更" : "保存済み";
            saveState.ForeColor = dirty ? Color.DarkGoldenrod : Color.DarkGreen;
            draftPath.Text = "途中保存先: " + project.DraftFile;
            undoButton.Enabled = !busy && editing.CanUndo;
            redoButton.Enabled = !busy && editing.CanRedo;
            finishButton.Enabled = !busy && count > 0;
        }

        void Insert(string where)
        {
            string source = SelectedSourceId;
            var steps = sourceSteps.SelectedItems.Cast<ListViewItem>().OrderBy(item => item.Index).Select(item => (long)item.Tag).ToArray();
            if (source == null || steps.Length == 0) { interaction.Message(this, "左側の元記録から差し込む手順を選択してください。Ctrl/Shiftで複数選択できます。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (where != "end")
            {
                if (rows.SelectedIndices.Count == 0) { interaction.Message(this, "差し込み先を完成順の一覧から選択してください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            }
            Change(() =>
            {
                editing.SelectRows(SelectedRowIds()); editing.SelectSourceSteps(source, steps);
                editing.Insert(where == "before" ? CompositionInsertPosition.Before : where == "after" ? CompositionInsertPosition.After : CompositionInsertPosition.End);
            });
        }

        void MoveRows(string where)
        {
            var ids = SelectedRowIds(); if (ids.Length == 0) return;
            Change(() =>
            {
                editing.SelectRows(ids);
                editing.Move(where == "first" ? CompositionMoveDirection.First : where == "last" ? CompositionMoveDirection.Last : where == "up" ? CompositionMoveDirection.Up : CompositionMoveDirection.Down);
            }, ids);
        }

        void SetIncluded(bool included)
        {
            var ids = SelectedRowIds(); if (ids.Length == 0) return;
            Change(() => editing.SetIncluded(ids, included), ids);
        }

        void AddNote()
        {
            string id = null;
            Run(() =>
            {
                CommitEditors(); editing.SelectRows(SelectedRowIds());
                id = editing.AddNote("補足説明", "");
                FillAll(new[] { id }); headline.Focus(); headline.SelectAll();
            });
        }

        void AddSource()
        {
            string path = interaction.SelectFolder(this, "追加する記録フォルダー（session.jsonがあるフォルダー）を選んでください。", outputRoot);
            if (path != null) Change(() => editing.AddSource(path, suggest.Checked));
        }

        void Undo()
        {
            Run(() =>
            {
                CommitEditors(); if (!editing.Undo()) return;
                FillAll(editing.SelectedRowIds.ToArray());
            });
        }

        void Redo()
        {
            Run(() =>
            {
                CommitEditors(); if (!editing.Redo()) return;
                FillAll(editing.SelectedRowIds.ToArray());
            });
        }

        bool SaveDraft()
        {
            bool success = false;
            Run(() =>
            {
                CommitEditors(); editing.Save(); success = true; FillAll();
            });
            return success;
        }

        void Complete()
        {
            Run(() =>
            {
                CommitEditors();
                // 完成後の作業保存失敗で、完成記録が作られた事実を見失わないよう先に途中保存する。
                string directory = editing.Complete(outputRoot);
                MergedDir = directory; finished = true; DialogResult = DialogResult.OK; Close();
            });
        }

        void ConfirmClose(object sender, FormClosingEventArgs e)
        {
            if (finished) return;
            if (busy) { e.Cancel = true; return; }
            CommitEditors();
            if (!HasUnsavedChanges) return;
            var answer = interaction.Message(this, "合成作業に未保存の変更があります。途中保存して閉じますか？\n「いいえ」は最後に保存した状態を残して閉じます。", Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            try
            {
                e.Cancel = !editing.TryClose(answer == DialogResult.Yes ? CompositionCloseDecision.Save : answer == DialogResult.No ? CompositionCloseDecision.Discard : CompositionCloseDecision.Cancel);
            }
            catch (Exception error)
            {
                e.Cancel = true;
                if (errorReporter != null) errorReporter(error);
                else interaction.Message(this, error.Message, WindowTitle.Create("合成作業を続けられません"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
