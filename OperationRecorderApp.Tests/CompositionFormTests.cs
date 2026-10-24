using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using OpRec;
using Xunit;

namespace OperationRecorderApp.Tests
{
    [CollectionDefinition("合成画面の専用操作", DisableParallelization = true)]
    public class CompositionFormCollection { }

    [Collection("合成画面の専用操作")]
    public class CompositionFormTests
    {
        static void Sta(Action action)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, true);
                    action();
                }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "合成画面の試験が完了しませんでした。");
            if (error != null) throw new Exception("合成画面の操作試験", error);
        }

        static void ReportError(Exception error) => throw new InvalidOperationException("合成画面の処理エラー", error);

        static T Find<T>(Form form, string name) where T : Control => (T)form.Controls.Find(name, true).Single();
        static void Pump() { Application.DoEvents(); Thread.Sleep(10); Application.DoEvents(); }
        static void Show(Form form)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.ShowInTaskbar = false;
            form.Show(); Pump();
        }
        static void Select(ListView list, params int[] indices)
        {
            foreach (var item in list.SelectedItems.Cast<ListViewItem>().ToArray()) item.Selected = false;
            foreach (int index in indices) list.Items[index].Selected = true;
            if (indices.Length > 0) list.Items[indices[0]].Focused = true;
            Pump();
        }
        static void Click(Form form, string name) { Find<Button>(form, name).PerformClick(); Pump(); }
        static CompositionProject Project(Form form) => (CompositionProject)typeof(MergeForm).GetField("project", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
        static Dictionary<string, string> Hashes(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(directory, file), file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));

        [Fact]
        public void 三記録の画像比較と範囲挿入移動除外を途中保存から完成できる()
        {
            using (var a = new TestSession())
            using (var b = new TestSession(marksOutput: true))
            using (var c = new TestSession(marksOutput: true))
            {
                var originals = new[] { a.Dir, b.Dir, c.Dir }.Select(Hashes).ToArray();
                var output = Path.Combine(a.Root, "completed");
                string draft = null, completed = null;
                Sta(() =>
                {
                    using (var form = new MergeForm(new[] { a.Dir, b.Dir, c.Dir }, output, ReportError))
                    {
                        Show(form);
                        var rows = Find<ListView>(form, "compositionRows");
                        Assert.Equal(4, rows.Items.Count);
                        Assert.Equal(3, Find<ComboBox>(form, "compositionSources").Items.Count);
                        Assert.False(form.HasUnsavedChanges);
                        Select(rows, 0);
                        var candidates = Find<ComboBox>(form, "compositionCandidate");
                        Assert.Equal(3, candidates.Items.Count);
                        candidates.SelectedIndex = 0; Pump();
                        Assert.Equal(0, Project(form).Rows[0].SelectedCandidate);
                        Assert.NotNull(Find<PictureBox>(form, "compositionBefore").Image);
                        Assert.NotNull(Find<PictureBox>(form, "compositionCompareBefore").Image);
                        var path = Project(form).GetStep(Project(form).Rows[0]).BeforePath;
                        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                        Find<TabControl>(form, "compositionPreviewTabs").SelectedIndex = 1; Pump();
                        Assert.Contains("比較:", Find<ComboBox>(form, "compositionComparison").Text);

                        Find<ComboBox>(form, "compositionSources").SelectedIndex = 2;
                        var sourceSteps = Find<ListView>(form, "compositionSourceSteps");
                        Select(sourceSteps, 0, 1); Select(rows, 2);
                        Click(form, "compositionInsertBefore");
                        Assert.Equal(6, rows.Items.Count);
                        var inserted = Project(form).Rows.Skip(2).Take(2).Select(row => row.Id).ToArray();
                        Select(rows, 2, 3); Click(form, "compositionMoveFirst");
                        Assert.Equal(inserted, Project(form).Rows.Take(2).Select(row => row.Id));
                        Click(form, "compositionExclude");
                        Assert.All(Project(form).Rows.Take(2), row => Assert.False(row.Included));
                        Click(form, "compositionUndo");
                        Assert.All(Project(form).Rows.Take(2), row => Assert.True(row.Included));
                        Click(form, "compositionRedo");
                        Assert.All(Project(form).Rows.Take(2), row => Assert.False(row.Included));
                        Select(rows, 0); Click(form, "compositionAddNote");
                        Find<TextBox>(form, "compositionHeadline").Text = "不足していた確認";
                        Find<TextBox>(form, "compositionBody").Text = "表示された内容を確認してから続行します。";
                        Find<TextBox>(form, "compositionCaption").Text = "三記録から完成した手順";
                        Find<TextBox>(form, "compositionDescription").Text = "途中保存後にも続けられる手順書";
                        Assert.True(form.HasUnsavedChanges);
                        Click(form, "compositionSave");
                        Assert.False(form.HasUnsavedChanges);
                        draft = form.DraftFile;
                        var saved = CompositionProject.Open(draft);
                        Assert.Equal(7, saved.Rows.Count);
                        Assert.Equal(5, saved.Rows.Count(row => row.Included));
                        var note = Assert.Single(saved.Rows, row => row.Candidates.Count == 0);
                        Assert.Equal("不足していた確認", saved.GetStep(note).Headline);
                        Assert.Contains("続行", saved.GetStep(note).Body);
                        Assert.Equal("三記録から完成した手順", saved.Caption);
                        form.Close(); Pump();
                    }
                });

                // 再開は元フォルダーを読み直さず、保存した画像・文章を使用する。
                var moved = new[] { a.Dir, b.Dir, c.Dir }.Select(directory => directory + "-moved").ToArray();
                for (int i = 0; i < moved.Length; i++) Directory.Move(new[] { a.Dir, b.Dir, c.Dir }[i], moved[i]);
                Sta(() =>
                {
                    using (var form = MergeForm.Resume(draft, output, ReportError))
                    {
                        Show(form);
                        Assert.False(form.HasUnsavedChanges);
                        Assert.Equal(7, Find<ListView>(form, "compositionRows").Items.Count);
                        Assert.NotNull(Find<PictureBox>(form, "compositionBefore").Image);
                        Click(form, "compositionFinish");
                        completed = form.MergedDir;
                        Assert.Equal(DialogResult.OK, form.DialogResult);
                        Assert.True(Directory.Exists(completed));
                    }
                });
                var result = ReportGenerator.ListSteps(completed);
                Assert.Equal(5, result.Count);
                Assert.Equal(new[] { 1, 2, 3, 4, 5 }, result.Select(step => step.Number));
                Assert.Contains(result, step => step.Headline == "不足していた確認" && step.Body.Contains("続行"));
                for (int i = 0; i < moved.Length; i++)
                {
                    var after = Hashes(moved[i]);
                    Assert.Equal(originals[i].Count, after.Count);
                    Assert.All(originals[i], pair => Assert.Equal(pair.Value, after[pair.Key]));
                }
            }
        }

        [Fact]
        public void 初回表示とハンドル再生成は採否を変えずチェック変更後も連続番号になる()
        {
            using (var a = new TestSession())
            {
                Sta(() =>
                {
                    using (var form = new MergeForm(new[] { a.Dir }, Path.Combine(a.Root, "output"), ReportError))
                    {
                        var project = Project(form);
                        string initial = project.CaptureState();
                        Show(form);
                        var rows = Find<ListView>(form, "compositionRows");
                        Assert.Equal(initial, project.CaptureState());
                        Assert.False(form.HasUnsavedChanges);
                        rows.Items[1].Checked = false; Pump();
                        Assert.False(project.Rows[1].Included);
                        Assert.Equal(new[] { "1", "—", "2", "3" }, rows.Items.Cast<ListViewItem>().Select(item => item.Text));
                        string state = project.CaptureState();
                        typeof(Control).GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(rows, null);
                        Pump();
                        Assert.Equal(state, project.CaptureState());
                        Assert.False(rows.Items[1].Checked);
                        Assert.Equal(new[] { "1", "—", "2", "3" }, rows.Items.Cast<ListViewItem>().Select(item => item.Text));
                        Click(form, "compositionSave");
                        form.Close(); Pump();
                    }
                });
            }
        }
        [Fact]
        public void 未編集で閉じられ最小画面でも保存と完成の操作が到達できる()
        {
            using (var a = new TestSession())
            {
                Sta(() =>
                {
                    using (var form = new MergeForm(new[] { a.Dir }, Path.Combine(a.Root, "output"), ReportError))
                    {
                        Show(form);
                        form.Size = form.MinimumSize;
                        Pump();
                        Assert.False(form.HasUnsavedChanges);
                        foreach (var name in new[] { "compositionSave", "compositionFinish", "compositionClose" })
                        {
                            var control = Find<Button>(form, name);
                            var bounds = form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                            Assert.True(bounds.Width > 0 && bounds.Height > 0 && form.ClientRectangle.Contains(bounds), name + "が画面内にありません。");
                        }
                        Assert.True(Find<ListView>(form, "compositionRows").Height > 100);
                        form.Close(); Pump();
                        Assert.False(form.Visible);
                    }
                });
            }
        }

        [Fact]
        public void 入力中の文章は選択変更時に保持され元に戻してやり直せる()
        {
            using (var a = new TestSession())
            {
                Sta(() =>
                {
                    using (var form = new MergeForm(new[] { a.Dir }, Path.Combine(a.Root, "output"), ReportError))
                    {
                        Show(form);
                        var rows = Find<ListView>(form, "compositionRows");
                        Select(rows, 0);
                        string original = Project(form).GetStep(Project(form).Rows[0]).Headline;
                        Find<TextBox>(form, "compositionHeadline").Text = "見出しを編集";
                        Find<TextBox>(form, "compositionBody").Text = "元の記録を変更しない補足";
                        Select(rows, 1);
                        Assert.Equal("見出しを編集", Project(form).GetStep(Project(form).Rows[0]).Headline);
                        Click(form, "compositionUndo");
                        Assert.Equal(original, Project(form).GetStep(Project(form).Rows[0]).Headline);
                        Click(form, "compositionRedo");
                        Assert.Equal("見出しを編集", Project(form).GetStep(Project(form).Rows[0]).Headline);
                        Click(form, "compositionSave");
                        form.Close(); Pump();
                    }
                });
            }
        }
    }
}