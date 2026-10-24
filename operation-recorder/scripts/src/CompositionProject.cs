using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpRec
{
    // 元データを変更しない、複数記録の合成作業。UIとは独立して保存・再開できる。
    public sealed partial class CompositionProject
    {
        public const int CurrentSchemaVersion = 1;
        const int MaximumSources = 128, MaximumSteps = 10000, MaximumRows = 30000;
        const long MaximumJsonBytes = 32L * 1024 * 1024;
        const long MaximumImageBytes = 256L * 1024 * 1024;
        static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        sealed class Document
        {
            public int SchemaVersion { get; set; } = CurrentSchemaVersion;
            public string Caption { get; set; } = "合成した記録";
            public string Description { get; set; } = "";
            public List<CompositionSource> Sources { get; set; } = new List<CompositionSource>();
            public List<CompositionRow> Rows { get; set; } = new List<CompositionRow>();
        }

        sealed class LoadedSource
        {
            public JsonObject Session;
            public List<JsonObject> Events;
            public Dictionary<long, JsonObject> BySeq;
            public List<ReportGenerator.StepInfo> Steps;
            public Dictionary<string, InventoryFile> Files;
        }

        Document document = new Document();
        Dictionary<string, LoadedSource> loaded = new Dictionary<string, LoadedSource>(StringComparer.Ordinal);
        bool buildingInitial;
        public string DirectoryPath { get; private set; }
        public string DraftFile => Path.Combine(DirectoryPath, "draft.json");
        public string Caption { get => document.Caption; set => document.Caption = value ?? ""; }
        public string Description { get => document.Description; set => document.Description = value ?? ""; }
        public List<CompositionSource> Sources => document.Sources;
        public List<CompositionRow> Rows => document.Rows;

        CompositionProject(string directory) { DirectoryPath = Path.GetFullPath(directory); }

        public static CompositionProject Create(IEnumerable<string> sessionDirs, string draftsRoot)
        {
            if (sessionDirs == null) throw new ArgumentNullException(nameof(sessionDirs));
            var dirs = sessionDirs.ToArray();
            if (dirs.Length == 0 || dirs.Length > MaximumSources) throw new IOException("合成する記録を1～128件選んでください。");
            var root = Path.GetFullPath(draftsRoot);
            Directory.CreateDirectory(root);
            CheckNoLinks(root);
            var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("合成作業の保存先が既に存在します。");
            Directory.CreateDirectory(directory);
            var project = new CompositionProject(directory);
            try
            {
                project.buildingInitial = true;
                foreach (var dir in dirs) project.AddSource(dir);
                project.buildingInitial = false;
                var first = project.loaded[project.Sources[0].Id].Session;
                project.Caption = Text(first["caption"]) + "(合成)";
                project.Description = Text(first["description"]);
                project.Save();
                return project;
            }
            catch
            {
                DeleteOwnedDirectory(directory, root);
                throw;
            }
        }

        public static CompositionProject Open(string draftJsonPath)
        {
            var path = Path.GetFullPath(draftJsonPath);
            if (!string.Equals(Path.GetFileName(path), "draft.json", StringComparison.OrdinalIgnoreCase))
                throw new IOException("合成作業のdraft.jsonを選んでください。");
            CheckNoLinks(path);
            var project = new CompositionProject(Path.GetDirectoryName(path));
            project.RestoreState(ReadJsonText(path));
            return project;
        }

        public IReadOnlyList<ReportGenerator.StepInfo> SourceSteps(string sourceId)
        {
            var source = Source(sourceId);
            return source.Steps.Select(CloneStep).ToArray();
        }

        public ReportGenerator.StepInfo GetStep(CompositionReference reference)
        {
            if (reference == null) throw new IOException("元手順の参照がありません。");
            var value = Source(reference.SourceId).Steps.SingleOrDefault(step => step.Seq == reference.Seq);
            if (value == null) throw new IOException("元の記録に指定された手順がありません。");
            return CloneStep(value);
        }

        public ReportGenerator.StepInfo GetStep(CompositionRow row)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            var result = row.Candidates.Count == 0
                ? new ReportGenerator.StepInfo { Kind = "note", Headline = "", Body = "", OriginalHeadline = "", OriginalBody = "" }
                : GetStep(Selected(row));
            if (row.HeadlineOverride != null) result.Headline = row.HeadlineOverride;
            if (row.BodyOverride != null) result.Body = row.BodyOverride;
            result.Edited |= row.HeadlineOverride != null || row.BodyOverride != null;
            return result;
        }

        public void AddSource(string dir, bool suggest = true)
        {
            if (Sources.Count >= MaximumSources) throw new IOException("合成元は128件までです。");
            var original = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
            CheckNoLinks(original);
            if (IsWithin(original, DirectoryPath)) throw new IOException("合成作業フォルダーを元記録として追加できません。");
            if (Sources.Any(s => string.Equals(s.OriginalDirectory, original, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("この記録は既に読み込まれています。手順一覧から必要な箇所を差し込んでください。");
            var id = Guid.NewGuid().ToString("N");
            var sourceDirectory = Path.Combine(DirectoryPath, "sources");
            Directory.CreateDirectory(sourceDirectory);
            var importing = Path.Combine(sourceDirectory, ".import-" + id);
            var final = Path.Combine(sourceDirectory, id);
            try
            {
                ImportSnapshot(original, importing);
                Directory.Move(importing, final);
                var source = ReadSnapshot(final);
                var descriptor = new CompositionSource { Id = id, Caption = Text(source.Session["caption"]), OriginalDirectory = original };
                // 候補作成はすべてメモリー上で済ませ、失敗時に既存の編集内容を変更しない。
                var newRows = suggest ? SuggestedRows(id, source) : new List<CompositionRow>(Rows);
                Sources.Add(descriptor);
                loaded.Add(id, source);
                document.Rows = newRows;
            }
            catch
            {
                DeleteOwnedDirectory(importing, sourceDirectory);
                DeleteOwnedDirectory(final, sourceDirectory);
                throw;
            }
        }

        List<CompositionRow> SuggestedRows(string sourceId, LoadedSource source)
        {
            if (Rows.Count == 0) return source.Events.Select(e => NewRow(sourceId, Seq(e))).ToList();
            // 手動の文章行も並びを保つ。新しい記録の候補を追加しても既存の選択・採否・文章を変更しない。
            var events = Rows.Select(row => row.Candidates.Count == 0
                ? new JsonObject { ["kind"] = "composition-note-" + row.Id }
                : Source(Selected(row).SourceId).BySeq[Selected(row).Seq]).ToList();
            if ((long)(events.Count + 1) * (source.Events.Count + 1) > 16000000)
                throw new IOException("自動突き合わせの手順数が多すぎます。自動候補なしで追加し、必要な範囲を差し込んでください。");
            var plan = SessionMerger.Plan(events, source.Events, false);
            var result = new List<CompositionRow>();
            foreach (var match in plan)
            {
                if (match.Kind == SessionMerger.RowKind.OnlyB) result.Add(NewRow(sourceId, Seq(source.Events[match.B])));
                else
                {
                    var row = CloneRow(Rows[match.A]);
                    if (match.Kind == SessionMerger.RowKind.Both)
                    {
                        row.Candidates.Add(new CompositionReference { SourceId = sourceId, Seq = Seq(source.Events[match.B]) });
                        if (buildingInitial) row.SelectedCandidate = row.Candidates.Count - 1;
                        // 初期案以降は既存行の選択を保持し、新しい候補の採用は利用者が確定する。
                    }
                    if (buildingInitial) row.Included = match.Kind == SessionMerger.RowKind.Both ? match.Include : row.Included && match.Include;
                    result.Add(row);
                }
            }
            if (result.Count > MaximumRows) throw new IOException("合成後の手順は30,000件までです。");
            return result;
        }

        public void Insert(string sourceId, IEnumerable<long> seqs, int index)
        {
            CheckIndex(index);
            if (seqs == null) throw new ArgumentNullException(nameof(seqs));
            var source = Source(sourceId);
            var values = seqs.ToArray();
            if (values.Any(seq => !source.BySeq.ContainsKey(seq))) throw new IOException("差し込む元手順がありません。");
            if (Rows.Count + values.Length > MaximumRows) throw new IOException("合成後の手順は30,000件までです。");
            Rows.InsertRange(index, values.Select(seq => NewRow(sourceId, seq)));
        }

        public void Move(IEnumerable<string> rowIds, int targetIndex)
        {
            CheckIndex(targetIndex);
            var ids = new HashSet<string>(rowIds ?? throw new ArgumentNullException(nameof(rowIds)), StringComparer.Ordinal);
            if (ids.Any(id => Rows.All(row => row.Id != id))) throw new IOException("移動する手順がありません。");
            var moving = Rows.Where(row => ids.Contains(row.Id)).ToList();
            var before = Rows.Take(targetIndex).Count(row => ids.Contains(row.Id));
            Rows.RemoveAll(row => ids.Contains(row.Id));
            Rows.InsertRange(targetIndex - before, moving);
        }

        public void AddNote(string headline, string body, int index)
        {
            CheckIndex(index);
            if (Rows.Count >= MaximumRows) throw new IOException("合成後の手順は30,000件までです。");
            Rows.Insert(index, new CompositionRow { Id = Guid.NewGuid().ToString("N"), HeadlineOverride = headline ?? "", BodyOverride = body ?? "" });
        }

        public void Choose(string rowId, int candidateIndex)
        {
            var row = Rows.SingleOrDefault(value => value.Id == rowId) ?? throw new IOException("選択する手順がありません。");
            if (candidateIndex < 0 || candidateIndex >= row.Candidates.Count) throw new IOException("選択する候補がありません。");
            row.SelectedCandidate = candidateIndex;
        }

        public string CaptureState() => JsonSerializer.Serialize(document, JsonOptions);

        public void RestoreState(string json)
        {
            if (json == null || System.Text.Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes) throw new IOException("作業JSONが容量上限を超えています。");
            Document candidate;
            try { candidate = JsonSerializer.Deserialize<Document>(json, JsonOptions); }
            catch (JsonException error) { throw new IOException("合成作業のJSONが壊れています。", error); }
            var candidateLoaded = Validate(candidate);
            document = candidate;
            loaded = candidateLoaded;
        }

        public void Save()
        {
            var verified = Validate(document);
            var text = CaptureState();
            if (System.Text.Encoding.UTF8.GetByteCount(text) > MaximumJsonBytes) throw new IOException("作業JSONが容量上限を超えています。");
            AtomicWrite(DraftFile, text);
            loaded = verified;
        }

        Dictionary<string, LoadedSource> Validate(Document value)
        {
            CheckNoLinks(DirectoryPath);
            if (value == null || value.SchemaVersion != CurrentSchemaVersion) throw new IOException("対応していない合成作業の版です。");
            if (value.Sources == null || value.Rows == null || value.Sources.Count == 0 || value.Sources.Count > MaximumSources || value.Rows.Count > MaximumRows)
                throw new IOException("合成作業の記録・手順数が不正です。");
            if (value.Caption == null || value.Description == null || value.Caption.Length > 10000 || value.Description.Length > 1000000)
                throw new IOException("記録の名前または説明が不正です。");
            var sources = new Dictionary<string, LoadedSource>(StringComparer.Ordinal);
            foreach (var source in value.Sources)
            {
                if (source == null || !IsId(source.Id) || string.IsNullOrEmpty(source.OriginalDirectory) || !Path.IsPathFullyQualified(source.OriginalDirectory) || sources.ContainsKey(source.Id))
                    throw new IOException("合成元の参照が不正です。");
                sources.Add(source.Id, ReadSnapshot(Path.Combine(DirectoryPath, "sources", source.Id)));
            }
            var rowIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in value.Rows)
            {
                if (row == null || !IsId(row.Id) || !rowIds.Add(row.Id) || row.Candidates == null || row.Candidates.Count > MaximumSources ||
                    (row.HeadlineOverride?.Length ?? 0) > 1000000 || (row.BodyOverride?.Length ?? 0) > 1000000)
                    throw new IOException("合成手順の形式が不正です。");
                if (row.Candidates.Count == 0)
                {
                    if (row.SelectedCandidate != 0) throw new IOException("説明手順の選択が不正です。");
                }
                else
                {
                    if (row.SelectedCandidate < 0 || row.SelectedCandidate >= row.Candidates.Count) throw new IOException("合成手順の採用候補が不正です。");
                    var unique = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var reference in row.Candidates)
                        if (reference == null || !sources.TryGetValue(reference.SourceId ?? "", out var src) || !src.BySeq.ContainsKey(reference.Seq) || !unique.Add(reference.SourceId + ":" + reference.Seq))
                            throw new IOException("合成手順の元記録・手順番号が不正です。");
                }
            }
            return sources;
        }

        LoadedSource Source(string id) => id != null && loaded.TryGetValue(id, out var source) ? source : throw new IOException("指定された元記録がありません。");
        void CheckIndex(int index) { if (index < 0 || index > Rows.Count) throw new ArgumentOutOfRangeException(nameof(index)); }
        static CompositionReference Selected(CompositionRow row) => row.SelectedCandidate >= 0 && row.SelectedCandidate < row.Candidates.Count ? row.Candidates[row.SelectedCandidate] : throw new IOException("採用候補が不正です。");
        static CompositionRow NewRow(string sourceId, long seq) => new CompositionRow { Id = Guid.NewGuid().ToString("N"), Candidates = new List<CompositionReference> { new CompositionReference { SourceId = sourceId, Seq = seq } } };
        static CompositionRow CloneRow(CompositionRow row) => JsonSerializer.Deserialize<CompositionRow>(JsonSerializer.Serialize(row, JsonOptions), JsonOptions);
        static ReportGenerator.StepInfo CloneStep(ReportGenerator.StepInfo step)
        {
            var result = new ReportGenerator.StepInfo();
            foreach (var field in typeof(ReportGenerator.StepInfo).GetFields()) field.SetValue(result, field.GetValue(step));
            return result;
        }
        static bool IsId(string value) => Guid.TryParseExact(value, "N", out _);
        static string Text(JsonNode node) => node == null ? "" : node.ToString();
        static long Seq(JsonObject value)
        {
            if (value?["seq"] is JsonValue seq && seq.TryGetValue<long>(out var number) && number > 0) return number;
            throw new IOException("記録の手順番号が不正です。");
        }
    }
}
