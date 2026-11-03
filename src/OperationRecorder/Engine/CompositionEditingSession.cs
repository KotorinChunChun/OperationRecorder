using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpRec
{
    public enum CompositionInsertPosition { Before, After, End }
    public enum CompositionMoveDirection { First, Up, Down, Last }
    public enum CompositionCloseDecision { Save, Discard, Cancel }

    // GUIとCLIが共有する編集セッション。描画・ダイアログ・OS入力には依存しない。
    public sealed class CompositionEditingSession
    {
        public sealed class TextBuffer
        {
            public string RowId { get; set; }
            public string Caption { get; set; }
            public string Description { get; set; }
            public string Headline { get; set; }
            public string Body { get; set; }
        }

        readonly Stack<string> undo = new Stack<string>(), redo = new Stack<string>();
        string savedState;
        TextBuffer pending;
        bool atomicEditing;
        string[] selectedRows = Array.Empty<string>();
        long[] selectedSeqs = Array.Empty<long>();
        public CompositionProject Project { get; }
        public string SelectedSourceId { get; private set; }
        public IReadOnlyList<string> SelectedRowIds => selectedRows.ToArray();
        public IReadOnlyList<long> SelectedSeqs => selectedSeqs.ToArray();
        public bool CanUndo => pending != null || undo.Count > 0;
        public bool CanRedo => redo.Count > 0;
        public bool IsDirty => PendingChanges() || Project.CaptureState() != savedState;
        public string Caption => pending?.Caption ?? Project.Caption;
        public string Description => pending?.Description ?? Project.Description;
        public TextBuffer PendingText => pending == null ? null : new TextBuffer
        {
            RowId = pending.RowId, Caption = pending.Caption, Description = pending.Description,
            Headline = pending.Headline, Body = pending.Body,
        };

        public CompositionEditingSession(CompositionProject project)
        {
            Project = project ?? throw new ArgumentNullException(nameof(project));
            savedState = Project.CaptureState();
            SelectedSourceId = Project.Sources.FirstOrDefault()?.Id;
            if (Project.Rows.Count > 0) selectedRows = new[] { Project.Rows[0].Id };
        }
        public static CompositionEditingSession Create(IEnumerable<string> sources, string draftsRoot) =>
            new CompositionEditingSession(CompositionProject.Create(sources, draftsRoot));
        public static CompositionEditingSession Open(string draft) => new CompositionEditingSession(CompositionProject.Open(draft));

        // JSONの1命令が選択変更と編集をまとめる場合も、失敗時は入力途中の文章や履歴まで戻す。
        // 保存・完成は永続化の契約が別なので、この編集トランザクション内では呼ばない。
        public void RunAtomically(Action<CompositionEditingSession> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (atomicEditing) throw new InvalidOperationException("編集トランザクションを入れ子にできません。");
            string state = Project.CaptureState(), saved = savedState;
            var buffer = PendingText; var selected = selectedRows.ToArray(); var seqs = selectedSeqs.ToArray();
            string source = SelectedSourceId;
            var oldUndo = undo.ToArray(); var oldRedo = redo.ToArray();
            atomicEditing = true;
            try { action(this); }
            catch (Exception operationError)
            {
                Exception restoreError = null;
                try { if (Project.CaptureState() != state) Project.RestoreState(state); }
                catch (Exception error) { restoreError = error; }
                finally
                {
                    pending = buffer; selectedRows = selected; selectedSeqs = seqs; SelectedSourceId = source; savedState = saved;
                    RestoreHistory(undo, oldUndo); RestoreHistory(redo, oldRedo);
                }
                if (restoreError != null) throw new AggregateException("編集操作と元状態への復元に失敗しました。保存済み作業を確認してください。", operationError, restoreError);
                throw;
            }
            finally { atomicEditing = false; }
        }
        static void RestoreHistory(Stack<string> destination, string[] values)
        {
            destination.Clear(); for (int i = values.Length - 1; i >= 0; i--) destination.Push(values[i]);
        }

        CompositionRow Row(string id) => Project.Rows.SingleOrDefault(row => row.Id == id) ?? throw new ArgumentException("選択する手順がありません。", nameof(id));
        string[] OrderedRows(IEnumerable<string> ids)
        {
            if (ids == null) throw new ArgumentNullException(nameof(ids));
            var wanted = new HashSet<string>(ids, StringComparer.Ordinal);
            if (wanted.Any(id => id == null || Project.Rows.All(row => row.Id != id))) throw new ArgumentException("選択する手順がありません。", nameof(ids));
            return Project.Rows.Where(row => wanted.Contains(row.Id)).Select(row => row.Id).ToArray();
        }
        int[] SelectionIndices() => Project.Rows.Select((row, index) => new { row.Id, Index = index })
            .Where(item => selectedRows.Contains(item.Id)).Select(item => item.Index).ToArray();

        public void SelectRows(IEnumerable<string> ids)
        {
            var ordered = OrderedRows(ids);
            CommitPending(); selectedRows = ordered;
        }
        public void SelectSourceSteps(string sourceId, IEnumerable<long> seqs)
        {
            if (seqs == null) throw new ArgumentNullException(nameof(seqs));
            var wanted = new HashSet<long>(seqs);
            var available = Project.SourceSteps(sourceId);
            if (wanted.Any(seq => available.All(step => step.Seq != seq))) throw new ArgumentException("選択する元手順がありません。", nameof(seqs));
            SelectedSourceId = sourceId;
            selectedSeqs = available.Where(step => wanted.Contains(step.Seq)).Select(step => step.Seq).ToArray();
        }
        public void StageText(string rowId, string caption, string description, string headline, string body)
        {
            if (rowId != null) Row(rowId);
            if (pending != null && pending.RowId != rowId) CommitPending();
            pending = new TextBuffer { RowId = rowId, Caption = caption ?? "", Description = description ?? "", Headline = headline ?? "", Body = body ?? "" };
            if (!PendingChanges()) pending = null;
        }
        bool PendingChanges()
        {
            if (pending == null) return false;
            if (pending.Caption != Project.Caption || pending.Description != Project.Description) return true;
            if (pending.RowId == null) return false;
            var value = Project.GetStep(Row(pending.RowId));
            return pending.Headline != (value.Headline ?? "") || pending.Body != (value.Body ?? "");
        }
        public ReportGenerator.StepInfo PreviewStep(CompositionRow row)
        {
            var value = Project.GetStep(row);
            if (pending != null && pending.RowId == row.Id)
            {
                value.Headline = pending.Headline; value.Body = pending.Body;
            }
            return value;
        }
        public void CommitPending()
        {
            if (pending == null) return;
            var edit = pending;
            if (PendingChanges())
            {
                Change(() =>
                {
                    var row = edit.RowId == null ? null : Row(edit.RowId);
                    var value = row == null ? null : Project.GetStep(row);
                    Project.Caption = edit.Caption; Project.Description = edit.Description;
                    if (row != null)
                    {
                        if ((value.Headline ?? "") != edit.Headline) row.HeadlineOverride = edit.Headline;
                        if ((value.Body ?? "") != edit.Body) row.BodyOverride = edit.Body;
                    }
                });
            }
            pending = null;
        }
        void Change(Action action)
        {
            string previous = Project.CaptureState();
            try { action(); }
            catch
            {
                if (Project.CaptureState() != previous) Project.RestoreState(previous);
                throw;
            }
            if (Project.CaptureState() != previous)
            {
                undo.Push(previous); Trim(undo); redo.Clear();
            }
        }
        static void Trim(Stack<string> history)
        {
            var keep = new List<string>(); long count = 0;
            foreach (var state in history)
            {
                if (keep.Count >= 60 || keep.Count > 0 && count + state.Length > 8 * 1024 * 1024) break;
                keep.Add(state); count += state.Length;
            }
            history.Clear(); for (int i = keep.Count - 1; i >= 0; i--) history.Push(keep[i]);
        }
        void NormalizeSelection()
        {
            selectedRows = Project.Rows.Where(row => selectedRows.Contains(row.Id)).Select(row => row.Id).ToArray();
            if (selectedRows.Length == 0 && Project.Rows.Count > 0) selectedRows = new[] { Project.Rows[0].Id };
            if (Project.Sources.All(source => source.Id != SelectedSourceId)) { SelectedSourceId = Project.Sources.FirstOrDefault()?.Id; selectedSeqs = Array.Empty<long>(); }
        }

        public void Choose(string rowId, int candidate)
        {
            var row = Row(rowId);
            if (candidate < 0 || candidate >= row.Candidates.Count) throw new ArgumentOutOfRangeException(nameof(candidate), "採用する候補がありません。");
            CommitPending(); Change(() => Project.Choose(rowId, candidate));
        }
        public void AddSource(string directory, bool suggest = true) { CommitPending(); Change(() => Project.AddSource(directory, suggest)); }
        public void Insert(CompositionInsertPosition position)
        {
            if (!Enum.IsDefined(typeof(CompositionInsertPosition), position)) throw new ArgumentOutOfRangeException(nameof(position));
            if (SelectedSourceId == null || selectedSeqs.Length == 0) throw new InvalidOperationException("差し込む元手順を選択してください。");
            var indices = SelectionIndices();
            if (position != CompositionInsertPosition.End && indices.Length == 0) throw new InvalidOperationException("差し込み先を選択してください。");
            int index = position == CompositionInsertPosition.End ? Project.Rows.Count : position == CompositionInsertPosition.Before ? indices.Min() : indices.Max() + 1;
            InsertAt(SelectedSourceId, selectedSeqs, index);
        }
        public void InsertAt(string sourceId, IEnumerable<long> seqs, int index)
        {
            if (seqs == null) throw new ArgumentNullException(nameof(seqs));
            var values = seqs.ToArray();
            if (index < 0 || index > Project.Rows.Count) throw new ArgumentOutOfRangeException(nameof(index));
            var available = Project.SourceSteps(sourceId);
            if (values.Length == 0) throw new ArgumentException("差し込む元手順を選択してください。", nameof(seqs));
            if (values.Any(seq => available.All(step => step.Seq != seq))) throw new ArgumentException("差し込む元手順がありません。", nameof(seqs));
            CommitPending(); Change(() => Project.Insert(sourceId, values, index));
        }
        public void Move(CompositionMoveDirection direction)
        {
            if (!Enum.IsDefined(typeof(CompositionMoveDirection), direction)) throw new ArgumentOutOfRangeException(nameof(direction));
            var indices = SelectionIndices(); if (indices.Length == 0) throw new InvalidOperationException("移動する手順を選択してください。");
            int target = direction == CompositionMoveDirection.First ? 0 : direction == CompositionMoveDirection.Last ? Project.Rows.Count :
                direction == CompositionMoveDirection.Up ? Math.Max(0, indices.Min() - 1) : Math.Min(Project.Rows.Count, indices.Max() + 2);
            MoveTo(target);
        }
        public void MoveTo(int targetIndex)
        {
            if (targetIndex < 0 || targetIndex > Project.Rows.Count) throw new ArgumentOutOfRangeException(nameof(targetIndex));
            if (selectedRows.Length == 0) throw new InvalidOperationException("移動する手順を選択してください。");
            CommitPending(); Change(() => Project.Move(selectedRows, targetIndex));
        }
        public void SetIncluded(IEnumerable<string> ids, bool included)
        {
            var values = OrderedRows(ids); CommitPending();
            Change(() => { foreach (var row in Project.Rows.Where(row => values.Contains(row.Id))) row.Included = included; });
        }
        public string AddNote(string headline, string body, int? index = null)
        {
            var indices = SelectionIndices();
            int at = index ?? (indices.Length == 0 ? Project.Rows.Count : indices.Max() + 1);
            if (at < 0 || at > Project.Rows.Count) throw new ArgumentOutOfRangeException(nameof(index));
            CommitPending(); Change(() => Project.AddNote(headline, body, at));
            selectedRows = new[] { Project.Rows[at].Id }; return selectedRows[0];
        }
        public bool Undo()
        {
            CommitPending(); if (undo.Count == 0) return false;
            string previous = Project.CaptureState(); Project.RestoreState(undo.Peek()); undo.Pop(); redo.Push(previous); Trim(redo); NormalizeSelection(); return true;
        }
        public bool Redo()
        {
            CommitPending(); if (redo.Count == 0) return false;
            string previous = Project.CaptureState(); Project.RestoreState(redo.Peek()); redo.Pop(); undo.Push(previous); Trim(undo); NormalizeSelection(); return true;
        }
        public void Save()
        {
            if (atomicEditing) throw new InvalidOperationException("保存は編集トランザクションの外で実行してください。");
            CommitPending(); Project.Save(); savedState = Project.CaptureState();
        }
        public string Complete(string outputRoot) { Save(); return Project.Complete(outputRoot); }
        public bool TryClose(CompositionCloseDecision decision)
        {
            if (!Enum.IsDefined(typeof(CompositionCloseDecision), decision)) throw new ArgumentOutOfRangeException(nameof(decision));
            CommitPending(); if (!IsDirty) return true;
            if (decision == CompositionCloseDecision.Cancel) return false;
            if (decision == CompositionCloseDecision.Save) Save();
            return true;
        }
    }
}
