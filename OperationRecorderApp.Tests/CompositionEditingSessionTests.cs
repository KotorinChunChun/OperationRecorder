using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using OpRec;
using Xunit;

namespace OperationRecorderApp.Tests
{
    internal sealed class CompositionAutomationFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "oprec-editing-session-" + Guid.NewGuid().ToString("N"));
        public AutomationWorkspace Workspace { get; }
        public string[] Sources { get; }
        public CompositionAutomationFixture(int count = 3)
        {
            Workspace = new AutomationWorkspace(Root);
            Sources = new string[count];
            for (int i = 0; i < count; i++)
            {
                using (var source = new TestSession(marksOutput: i % 2 == 0))
                {
                    string target = Workspace.EnsureDirectory("inputs/source-" + i);
                    foreach (var file in Directory.GetFiles(source.Dir, "*", SearchOption.AllDirectories))
                    {
                        string destination = Path.Combine(target, Path.GetRelativePath(source.Dir, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(file, destination);
                    }
                    Sources[i] = target;
                }
            }
        }
        public CompositionEditingSession Create(int count = 3) => CompositionEditingSession.Create(Sources.Take(count), Workspace.EnsureDirectory("compositions"));
        public Dictionary<string, string> InputHashes() => Directory.GetFiles(Path.Combine(Root, "inputs"), "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(Root, file), file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    public sealed class CompositionEditingSessionTests
    {
        [Fact, Trait("CaseId", "COMP-SESSION-001")]
        public void 入力途中の文章を選択変更で確定しUndoRedo保存時点を共有する()
        {
            using var fixture = new CompositionAutomationFixture(); var editing = fixture.Create();
            var row = editing.Project.Rows[0]; string original = editing.Project.GetStep(row).Headline;
            Assert.False(editing.IsDirty); Assert.False(editing.CanUndo);
            editing.StageText(row.Id, "編集中の名前", "説明", "共通の編集見出し", "共通の本文");
            Assert.True(editing.IsDirty); Assert.True(editing.CanUndo); Assert.Equal("共通の編集見出し", editing.PreviewStep(row).Headline);
            Assert.Equal(original, editing.Project.GetStep(row).Headline);
            editing.SelectRows(new[] { editing.Project.Rows[1].Id });
            Assert.Equal("共通の編集見出し", editing.Project.GetStep(row).Headline);
            Assert.True(editing.Undo()); Assert.Equal(original, editing.Project.GetStep(editing.Project.Rows[0]).Headline);
            Assert.True(editing.Redo()); Assert.Equal("共通の編集見出し", editing.Project.GetStep(editing.Project.Rows[0]).Headline);
            editing.Save(); Assert.False(editing.IsDirty);
            Assert.True(editing.Undo()); Assert.True(editing.IsDirty);
            Assert.True(editing.Redo()); Assert.False(editing.IsDirty);
        }

        [Fact, Trait("CaseId", "COMP-SESSION-002")]
        public void 選択範囲の差込移動採否をGUIと同じ命令で編集する()
        {
            using var fixture = new CompositionAutomationFixture(); var editing = fixture.Create();
            var original = editing.Project.Rows.Select(row => row.Id).ToArray();
            editing.SelectSourceSteps(editing.Project.Sources[1].Id, new long[] { 2, 1 });
            Assert.Equal(new long[] { 1, 2 }, editing.SelectedSeqs);
            editing.SelectRows(new[] { original[2] }); editing.Insert(CompositionInsertPosition.Before);
            Assert.Equal(6, editing.Project.Rows.Count);
            var inserted = editing.Project.Rows.Skip(2).Take(2).Select(row => row.Id).ToArray();
            editing.SelectRows(inserted.Reverse()); editing.Move(CompositionMoveDirection.First);
            Assert.Equal(inserted, editing.Project.Rows.Take(2).Select(row => row.Id));
            editing.Move(CompositionMoveDirection.Down);
            Assert.Equal(inserted, editing.Project.Rows.Skip(1).Take(2).Select(row => row.Id));
            editing.SetIncluded(inserted, false); Assert.All(editing.Project.Rows.Where(row => inserted.Contains(row.Id)), row => Assert.False(row.Included));
            editing.SetIncluded(inserted, true); Assert.All(editing.Project.Rows.Where(row => inserted.Contains(row.Id)), row => Assert.True(row.Included));
        }

        [Fact, Trait("CaseId", "COMP-SESSION-003")]
        public void 複合命令の失敗は選択入力バッファ履歴まで元へ戻す()
        {
            using var fixture = new CompositionAutomationFixture(); var editing = fixture.Create();
            var first = editing.Project.Rows[0].Id; var second = editing.Project.Rows[1].Id;
            editing.StageText(first, "入力中", "", "未確定見出し", "未確定本文");
            string before = editing.Project.CaptureState(); var selected = editing.SelectedRowIds.ToArray();
            Assert.Throws<ArgumentOutOfRangeException>(() => editing.RunAtomically(session =>
            {
                session.SelectRows(new[] { second }); session.Choose(second, 999);
            }));
            Assert.Equal(before, editing.Project.CaptureState()); Assert.Equal(selected, editing.SelectedRowIds);
            Assert.Equal("未確定見出し", editing.PendingText.Headline); Assert.True(editing.IsDirty);
            editing.CommitPending(); Assert.True(editing.Undo()); Assert.False(editing.IsDirty); Assert.False(editing.CanUndo);
        }

        [Fact, Trait("CaseId", "COMP-SESSION-004")]
        public void 保存失敗と閉じる三択を標準ダイアログなしで検証できる()
        {
            using var fixture = new CompositionAutomationFixture(); var editing = fixture.Create();
            string saved = File.ReadAllText(editing.Project.DraftFile);
            editing.StageText(editing.Project.Rows[0].Id, "保存前", "", "編集", "本文");
            Assert.False(editing.TryClose(CompositionCloseDecision.Cancel));
            Assert.True(editing.IsDirty); Assert.Equal(saved, File.ReadAllText(editing.Project.DraftFile));
            using (var locked = new FileStream(editing.Project.DraftFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.ThrowsAny<IOException>(() => editing.TryClose(CompositionCloseDecision.Save));
            Assert.True(editing.IsDirty); Assert.Equal(saved, File.ReadAllText(editing.Project.DraftFile));
            Assert.True(editing.TryClose(CompositionCloseDecision.Discard));
            Assert.Equal(saved, File.ReadAllText(editing.Project.DraftFile));
            Assert.True(editing.TryClose(CompositionCloseDecision.Save)); Assert.False(editing.IsDirty);
            Assert.Equal("保存前", CompositionEditingSession.Open(editing.Project.DraftFile).Caption);
        }

        [Fact, Trait("CaseId", "COMP-SESSION-005")]
        public void 保存後の再開と完成は原記録を残し文章画像出典を引き継ぐ()
        {
            using var fixture = new CompositionAutomationFixture(); var before = fixture.InputHashes(); var editing = fixture.Create();
            editing.Choose(editing.Project.Rows[0].Id, 0);
            editing.SetIncluded(new[] { editing.Project.Rows[1].Id }, false);
            editing.SelectRows(new[] { editing.Project.Rows[0].Id }); editing.AddNote("説明を補完", "不足分の説明");
            editing.Save(); var reopened = CompositionEditingSession.Open(editing.Project.DraftFile);
            Assert.False(reopened.IsDirty); Assert.False(reopened.CanUndo);
            string complete = reopened.Complete(fixture.Workspace.EnsureDirectory("records"));
            var steps = ReportGenerator.ListSteps(complete);
            Assert.Equal(4, steps.Count); Assert.Contains(steps, step => step.Headline == "説明を補完" && step.Body == "不足分の説明");
            Assert.All(steps.Where(step => step.BeforePath != null), step => Assert.True(File.Exists(step.BeforePath)));
            Assert.Equal(before.OrderBy(pair => pair.Key), fixture.InputHashes().OrderBy(pair => pair.Key));
        }

        [Fact, Trait("CaseId", "COMP-SESSION-006")]
        public void 元記録の追加と履歴復元は保存後に元を移動しても使える()
        {
            using var fixture = new CompositionAutomationFixture(); var editing = fixture.Create(1);
            editing.AddSource(fixture.Sources[1], false); Assert.Equal(2, editing.Project.Sources.Count);
            Assert.True(editing.Undo()); Assert.Single(editing.Project.Sources);
            Assert.True(editing.Redo()); Assert.Equal(2, editing.Project.Sources.Count);
            editing.Save(); Directory.Move(fixture.Sources[1], fixture.Sources[1] + "-moved");
            var reopened = CompositionEditingSession.Open(editing.Project.DraftFile);
            reopened.SelectSourceSteps(reopened.Project.Sources[1].Id, new long[] { 1 }); reopened.Insert(CompositionInsertPosition.End);
            Assert.Equal(5, ReportGenerator.ListSteps(reopened.Complete(fixture.Workspace.EnsureDirectory("records"))).Count);
        }

        [Fact, Trait("CaseId", "COMP-SESSION-007")]
        public void 不正な選択や列挙値は既存の状態を変えない()
        {
            using var fixture = new CompositionAutomationFixture(); var editing = fixture.Create(); string state = editing.Project.CaptureState();
            Assert.Throws<ArgumentException>(() => editing.SelectRows(new[] { "missing" }));
            Assert.Throws<ArgumentException>(() => editing.SelectSourceSteps(editing.Project.Sources[0].Id, new long[] { 999 }));
            Assert.Throws<ArgumentException>(() => editing.InsertAt(editing.Project.Sources[0].Id, Array.Empty<long>(), 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => editing.Move((CompositionMoveDirection)999));
            Assert.Throws<ArgumentOutOfRangeException>(() => editing.Insert((CompositionInsertPosition)999));
            Assert.Throws<ArgumentOutOfRangeException>(() => editing.TryClose((CompositionCloseDecision)999));
            Assert.Throws<ArgumentException>(() => editing.SetIncluded(new[] { editing.Project.Rows[0].Id, "missing" }, false));
            Assert.Equal(state, editing.Project.CaptureState()); Assert.False(editing.IsDirty);
        }

        [Fact, Trait("CaseId", "COMP-SESSION-008")]
        public void 入力を元へ戻すと未保存にならず異なる行への入力は前の行を保持する()
        {
            using var fixture = new CompositionAutomationFixture(); var editing = fixture.Create();
            var first = editing.Project.Rows[0]; var second = editing.Project.Rows[1]; var original = editing.Project.GetStep(first);
            editing.StageText(first.Id, editing.Caption, editing.Description, "変更", "本文");
            editing.StageText(first.Id, editing.Project.Caption, editing.Project.Description, original.Headline, original.Body);
            Assert.False(editing.IsDirty); Assert.False(editing.CanUndo);
            editing.StageText(first.Id, editing.Caption, editing.Description, "1つ目", "本文1");
            editing.StageText(second.Id, editing.Caption, editing.Description, "2つ目", "本文2"); editing.CommitPending();
            Assert.Equal("1つ目", editing.Project.GetStep(first).Headline); Assert.Equal("2つ目", editing.Project.GetStep(second).Headline);
        }

        [Fact, Trait("CaseId", "COMP-SESSION-009")]
        public void 編集トランザクションは不正な追加と永続操作を取り消す()
        {
            using var fixture = new CompositionAutomationFixture(); var editing = fixture.Create();
            string state = editing.Project.CaptureState(); string saved = File.ReadAllText(editing.Project.DraftFile);
            Assert.ThrowsAny<IOException>(() => editing.RunAtomically(session => session.AddSource(Path.Combine(fixture.Root, "missing"))));
            Assert.Throws<InvalidOperationException>(() => editing.RunAtomically(session => session.Save()));
            Assert.Equal(state, editing.Project.CaptureState()); Assert.Equal(saved, File.ReadAllText(editing.Project.DraftFile));
            Assert.False(editing.CanUndo); Assert.False(editing.IsDirty);
        }

        [Fact, Trait("CaseId", "COMP-SESSION-010")]
        public void 追加編集でRedoを破棄し取り消し件数を制限する()
        {
            using var fixture = new CompositionAutomationFixture(1); var editing = fixture.Create(1);
            editing.AddNote("追加", "説明"); Assert.True(editing.Undo()); Assert.True(editing.CanRedo);
            editing.AddNote("別の追加", "説明"); Assert.False(editing.CanRedo);
            for (int i = 0; i < 65; i++) editing.AddNote("補足" + i, "");
            int count = 0; while (editing.Undo()) count++;
            Assert.Equal(60, count);
        }
    }

    [Collection("合成画面の専用操作")]
    public sealed class CompositionSessionUiTests
    {
        sealed class Interaction : AppInteraction
        {
            public DialogResult Answer = DialogResult.No;
            public string Folder;
            public int Messages;
            public override DialogResult Message(IWin32Window owner, string text, string title, MessageBoxButtons buttons, MessageBoxIcon icon)
            {
                Messages++; return Answer;
            }
            public override string SelectFolder(IWin32Window owner, string description, string selectedPath) => Folder;
        }
        static void Sta(Action action)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, true); action(); }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "合成画面の試験が完了しませんでした。");
            if (error != null) throw new Exception("合成画面の共有セッション試験", error);
        }
        static T Find<T>(Control form, string name) where T : Control => (T)form.Controls.Find(name, true).Single();
        static void Pump() { Application.DoEvents(); Thread.Sleep(10); Application.DoEvents(); }
        static void Show(Form form)
        {
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-32000, -32000);
            form.ShowInTaskbar = false; form.Show(); Pump();
        }
        static void Select(ListView list, params int[] indices)
        {
            foreach (var item in list.SelectedItems.Cast<ListViewItem>().ToArray()) item.Selected = false;
            foreach (int index in indices) list.Items[index].Selected = true;
            Pump();
        }
        static void Click(Control form, string name) { Find<Button>(form, name).PerformClick(); Pump(); }

        [Theory, Trait("CaseId", "COMP-UI-001")]
        [InlineData(DialogResult.Yes)] [InlineData(DialogResult.No)] [InlineData(DialogResult.Cancel)]
        public void 画面から閉じる三択は共有サービスと保存済みの記録に反映する(DialogResult answer)
        {
            using var fixture = new CompositionAutomationFixture(1);
            Sta(() =>
            {
                var interaction = new Interaction { Answer = answer };
                using var form = new MergeForm(fixture.Sources, fixture.Workspace.EnsureDirectory("records"), error => throw error, interaction);
                Show(form); string draft = form.DraftFile; string saved = File.ReadAllText(draft);
                Find<TextBox>(form, "compositionHeadline").Text = "閉じる際の編集";
                Assert.True(form.EditingSession.IsDirty); form.Close(); Pump();
                Assert.Equal(1, interaction.Messages);
                if (answer == DialogResult.Cancel)
                {
                    Assert.False(form.IsDisposed); Assert.True(form.HasUnsavedChanges);
                    interaction.Answer = DialogResult.No; form.Close(); Pump();
                }
                else Assert.True(form.IsDisposed);
                var reopened = CompositionEditingSession.Open(draft);
                if (answer == DialogResult.Yes) Assert.Equal("閉じる際の編集", reopened.Project.GetStep(reopened.Project.Rows[0]).Headline);
                else Assert.Equal(saved, File.ReadAllText(draft));
            });
        }

        [Fact, Trait("CaseId", "COMP-UI-002")]
        public void 閉じる際の保存失敗は画面を残してエラーを返し再選択できる()
        {
            using var fixture = new CompositionAutomationFixture(1);
            Sta(() =>
            {
                var errors = new List<Exception>(); var interaction = new Interaction { Answer = DialogResult.Yes };
                using var form = new MergeForm(fixture.Sources, fixture.Workspace.EnsureDirectory("records"), errors.Add, interaction);
                Show(form); string saved = File.ReadAllText(form.DraftFile);
                Find<TextBox>(form, "compositionHeadline").Text = "保存できない編集";
                using (var locked = new FileStream(form.DraftFile, FileMode.Open, FileAccess.Read, FileShare.Read)) { form.Close(); Pump(); }
                Assert.False(form.IsDisposed); Assert.True(form.HasUnsavedChanges); Assert.Single(errors);
                Assert.IsAssignableFrom<IOException>(errors[0]); Assert.Equal(saved, File.ReadAllText(form.DraftFile));
                interaction.Answer = DialogResult.No; form.Close(); Pump(); Assert.True(form.IsDisposed);
            });
        }

        [Fact, Trait("CaseId", "COMP-UI-003")]
        public void 元手順と完成順の範囲選択を公開セッションへ伝え不足選択の案内も差し替えられる()
        {
            using var fixture = new CompositionAutomationFixture(2);
            Sta(() =>
            {
                var interaction = new Interaction();
                using var form = new MergeForm(fixture.Sources, fixture.Workspace.EnsureDirectory("records"), error => throw error, interaction);
                Show(form);
                Find<ComboBox>(form, "compositionSources").SelectedIndex = 1;
                var sourceSteps = Find<ListView>(form, "compositionSourceSteps");
                Select(sourceSteps, 3, 1);
                Assert.Equal(form.EditingSession.Project.Sources[1].Id, form.EditingSession.SelectedSourceId);
                Assert.Equal(new long[] { 2, 4 }, form.EditingSession.SelectedSeqs);
                var rows = Find<ListView>(form, "compositionRows"); Select(rows, 3, 1);
                Assert.Equal(new[] { form.EditingSession.Project.Rows[1].Id, form.EditingSession.Project.Rows[3].Id }, form.EditingSession.SelectedRowIds);
                Select(sourceSteps); Click(form, "compositionInsertBefore"); Assert.Equal(1, interaction.Messages);
                Assert.Equal(4, form.EditingSession.Project.Rows.Count); Assert.False(form.HasUnsavedChanges);
                form.Close();
            });
        }

        [Fact, Trait("CaseId", "COMP-UI-004")]
        public void 記録追加はフォルダー取消と失敗を通常の選択画面なしで検証する()
        {
            using var fixture = new CompositionAutomationFixture();
            Sta(() =>
            {
                var errors = new List<Exception>(); var interaction = new Interaction();
                using var form = new MergeForm(fixture.Sources.Take(2), fixture.Workspace.EnsureDirectory("records"), errors.Add, interaction);
                Show(form); Click(form, "compositionAddSource"); Assert.Equal(2, form.EditingSession.Project.Sources.Count);
                interaction.Folder = fixture.Sources[2]; Click(form, "compositionAddSource"); Assert.Equal(3, form.EditingSession.Project.Sources.Count);
                string previous = form.EditingSession.Project.CaptureState();
                interaction.Folder = Path.Combine(fixture.Root, "missing"); Click(form, "compositionAddSource");
                Assert.Single(errors); Assert.Equal(previous, form.EditingSession.Project.CaptureState());
                form.Close();
            });
        }
    }
}
