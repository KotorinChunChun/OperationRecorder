using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using OpRec;

namespace OperationRecorderApp
{
    // 自動試験用JSONをGUIと同じ編集セッションへ渡す。通常設定や名前付きMutexは使わない。
    public static class CompositionAutomation
    {
        static readonly Dictionary<string, string[]> Fields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["select"] = new[] { "rows", "row" },
            ["source"] = new[] { "source", "steps" },
            ["add-source"] = new[] { "path", "suggest" },
            ["choose"] = new[] { "row", "candidate" },
            ["insert"] = new[] { "source", "steps", "position", "index" },
            ["move"] = new[] { "rows", "row", "direction", "index" },
            ["include"] = new[] { "rows", "row", "included" },
            ["exclude"] = new[] { "rows", "row" },
            ["note"] = new[] { "headline", "body", "index" },
            ["text"] = new[] { "row", "caption", "description", "headline", "body" },
            ["commit"] = Array.Empty<string>(), ["undo"] = Array.Empty<string>(), ["redo"] = Array.Empty<string>(),
            ["save"] = Array.Empty<string>(), ["complete"] = Array.Empty<string>(),
            ["close"] = new[] { "decision" }, ["inspect"] = Array.Empty<string>(),
        };

        public static JsonObject Execute(JsonObject request, AutomationWorkspace workspace)
        {
            if (workspace == null) throw new ArgumentNullException(nameof(workspace));
            ValidateRequest(request);
            CompositionEditingSession session;
            if (request["draft"] != null) session = CompositionEditingSession.Open(workspace.Resolve(Text(request, "draft"), true));
            else
            {
                var sources = ((JsonArray)request["sources"]).Select(node => workspace.Resolve(String(node, "sources"), true)).ToArray();
                session = CompositionEditingSession.Create(sources, workspace.EnsureDirectory("compositions"));
            }
            var trace = new JsonArray();
            string completed = null;
            foreach (var node in (JsonArray)request["operations"] ?? new JsonArray())
            {
                var operation = (JsonObject)node;
                string type = Text(operation, "type");
                bool? accepted = null;
                Action execute = () =>
                {
                  switch (type)
                  {
                    case "select": session.SelectRows(RowIds(session, operation, true)); break;
                    case "source": session.SelectSourceSteps(SourceId(session, Int(operation, "source")), Longs(operation, "steps")); break;
                    case "add-source": session.AddSource(workspace.Resolve(Text(operation, "path"), true), Bool(operation, "suggest", true)); break;
                    case "choose": session.Choose(RowId(session, operation), Int(operation, "candidate")); break;
                    case "insert":
                        if (operation["source"] != null || operation["steps"] != null)
                            session.SelectSourceSteps(SourceId(session, Int(operation, "source")), Longs(operation, "steps"));
                        if (operation["index"] != null) session.InsertAt(session.SelectedSourceId, session.SelectedSeqs, Int(operation, "index"));
                        else session.Insert(InsertPosition(Text(operation, "position", "after")));
                        break;
                    case "move":
                        if (operation["rows"] != null || operation["row"] != null) session.SelectRows(RowIds(session, operation));
                        if (operation["index"] != null) session.MoveTo(Int(operation, "index"));
                        else session.Move(MoveDirection(Text(operation, "direction", "up")));
                        break;
                    case "include": session.SetIncluded(RowIds(session, operation), Bool(operation, "included", true)); break;
                    case "exclude": session.SetIncluded(RowIds(session, operation), false); break;
                    case "note": session.AddNote(Text(operation, "headline", "補足説明"), Text(operation, "body", ""), operation["index"] == null ? null : Int(operation, "index")); break;
                    case "text":
                        string rowId = operation["row"] != null ? RowId(session, operation) : session.SelectedRowIds.Count == 1 ? session.SelectedRowIds[0] : null;
                        if ((operation["headline"] != null || operation["body"] != null) && rowId == null) throw new ArgumentException("文章を編集する手順を1件選んでください。");
                        var row = rowId == null ? null : session.Project.Rows.Single(value => value.Id == rowId);
                        var step = row == null ? null : session.PreviewStep(row);
                        session.StageText(rowId, Text(operation, "caption", session.Caption), Text(operation, "description", session.Description),
                            Text(operation, "headline", step?.Headline ?? ""), Text(operation, "body", step?.Body ?? ""));
                        break;
                    case "commit": session.CommitPending(); break;
                    case "undo": accepted = session.Undo(); break;
                    case "redo": accepted = session.Redo(); break;
                    case "save": session.Save(); break;
                    case "complete": completed = session.Complete(workspace.EnsureDirectory("records")); break;
                    case "close": accepted = session.TryClose(CloseDecision(Text(operation, "decision"))); break;
                    case "inspect": break;
                  }
                };
                if (type == "save" || type == "complete" || type == "close" || type == "inspect") execute();
                else session.RunAtomically(_ => execute());
                var item = new JsonObject
                {
                    ["type"] = type, ["dirty"] = session.IsDirty, ["canUndo"] = session.CanUndo, ["canRedo"] = session.CanRedo,
                    ["sourceCount"] = session.Project.Sources.Count, ["rowCount"] = session.Project.Rows.Count,
                    ["includedCount"] = session.Project.Rows.Count(value => value.Included),
                };
                if (accepted.HasValue) item["accepted"] = accepted.Value;
                if (type == "inspect") item["state"] = Snapshot(session);
                trace.Add(item);
            }
            return new JsonObject { ["schemaVersion"] = 1, ["domain"] = "composition", ["draft"] = session.Project.DraftFile,
                ["completed"] = completed, ["operations"] = trace, ["state"] = Snapshot(session) };
        }

        public static JsonObject Snapshot(CompositionEditingSession session)
        {
            var project = session.Project;
            var sources = new JsonArray();
            for (int i = 0; i < project.Sources.Count; i++)
            {
                var source = project.Sources[i];
                sources.Add(new JsonObject { ["index"] = i, ["id"] = source.Id, ["caption"] = source.Caption,
                    ["originalDirectory"] = source.OriginalDirectory, ["stepCount"] = project.SourceSteps(source.Id).Count });
            }
            var rows = new JsonArray(); int number = 0;
            for (int i = 0; i < project.Rows.Count; i++)
            {
                var row = project.Rows[i]; var step = session.PreviewStep(row); var candidates = new JsonArray();
                for (int candidate = 0; candidate < row.Candidates.Count; candidate++)
                {
                    var reference = row.Candidates[candidate]; var original = project.GetStep(reference);
                    candidates.Add(new JsonObject { ["index"] = candidate, ["source"] = project.Sources.FindIndex(value => value.Id == reference.SourceId),
                        ["sourceId"] = reference.SourceId, ["seq"] = reference.Seq, ["headline"] = original.Headline,
                        ["body"] = original.Body, ["before"] = original.BeforePath, ["after"] = original.AfterPath });
                }
                rows.Add(new JsonObject { ["index"] = i, ["id"] = row.Id, ["number"] = row.Included ? ++number : null,
                    ["included"] = row.Included, ["selectedCandidate"] = row.SelectedCandidate, ["candidates"] = candidates,
                    ["headline"] = step.Headline, ["body"] = step.Body, ["before"] = step.BeforePath, ["after"] = step.AfterPath });
            }
            var selected = new JsonArray(); foreach (var id in session.SelectedRowIds) selected.Add(project.Rows.FindIndex(row => row.Id == id));
            var seqs = new JsonArray(); foreach (long seq in session.SelectedSeqs) seqs.Add(seq);
            return new JsonObject { ["caption"] = session.Caption, ["description"] = session.Description,
                ["dirty"] = session.IsDirty, ["canUndo"] = session.CanUndo, ["canRedo"] = session.CanRedo,
                ["selectedRows"] = selected, ["selectedSource"] = project.Sources.FindIndex(source => source.Id == session.SelectedSourceId),
                ["selectedSteps"] = seqs, ["sources"] = sources, ["rows"] = rows };
        }

        static void ValidateRequest(JsonObject request)
        {
            if (request == null || Int(request, "schemaVersion") != 1) throw new ArgumentException("合成シナリオのschemaVersionは1です。");
            Allowed(request, new[] { "schemaVersion", "sources", "draft", "operations" });
            bool hasSources = request["sources"] != null, hasDraft = request["draft"] != null;
            if (hasSources == hasDraft) throw new ArgumentException("sourcesかdraftのどちらか一方を指定してください。");
            if (hasSources)
            {
                if (!(request["sources"] is JsonArray sources) || sources.Count == 0 || sources.Count > 128) throw new ArgumentException("sourcesは1～128件の配列です。");
                foreach (var source in sources) String(source, "sources");
            }
            else Text(request, "draft");
            if (request["operations"] != null && !(request["operations"] is JsonArray)) throw new ArgumentException("operationsは配列です。");
            var operations = (JsonArray)request["operations"] ?? new JsonArray();
            if (operations.Count > 1000) throw new ArgumentException("操作は1000件までです。");
            foreach (var node in operations)
            {
                if (!(node is JsonObject operation)) throw new ArgumentException("操作はオブジェクトです。");
                string type = Text(operation, "type");
                if (!Fields.TryGetValue(type, out var fields)) throw new ArgumentException("知らない合成操作です：" + type);
                Allowed(operation, fields.Concat(new[] { "type" }).ToArray());
                foreach (var field in operation)
                {
                    if (field.Key == "type") continue;
                    if (field.Key == "rows") { foreach (var ignored in Ints(operation, field.Key)) { } }
                    else if (field.Key == "steps") { foreach (var ignored in Longs(operation, field.Key)) { } }
                    else if (new[] { "row", "source", "candidate", "index" }.Contains(field.Key)) Int(operation, field.Key);
                    else if (field.Key == "suggest" || field.Key == "included") Bool(operation, field.Key, true);
                    else String(field.Value, field.Key);
                }
                if (type == "source") { Int(operation, "source"); Longs(operation, "steps"); }
                if (type == "add-source") Text(operation, "path");
                if (type == "choose") Int(operation, "candidate");
                if (type == "insert" && (operation["source"] != null || operation["steps"] != null)) { Int(operation, "source"); Longs(operation, "steps"); }
                if (type == "insert" && operation["position"] != null) InsertPosition(Text(operation, "position"));
                if (type == "move" && operation["direction"] != null) MoveDirection(Text(operation, "direction"));
                if (operation["index"] != null && (operation["position"] != null || operation["direction"] != null))
                    throw new ArgumentException("位置のindexとposition/directionは同時に指定できません。");
                if (type == "close") CloseDecision(Text(operation, "decision"));
                if (type == "select" && operation["rows"] == null && operation["row"] == null) throw new ArgumentException("selectにはrowsかrowが必要です。");
                if (operation["rows"] != null && operation["row"] != null) throw new ArgumentException("rowsとrowは同時に指定できません。");
            }
        }
        static void Allowed(JsonObject value, string[] allowed)
        {
            if (value.Any(pair => !allowed.Contains(pair.Key))) throw new ArgumentException("知らないシナリオ項目です：" + value.First(pair => !allowed.Contains(pair.Key)).Key);
        }
        static string String(JsonNode node, string name) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw new ArgumentException(name + "は文字列です。");
        static string Text(JsonObject node, string name, string fallback = null) => node[name] != null ? String(node[name], name) : fallback ?? throw new ArgumentException(name + "が必要です。");
        static int Int(JsonObject node, string name) => node[name] is JsonValue value && value.TryGetValue<int>(out var number) && number >= 0 ? number : throw new ArgumentException(name + "は0以上の整数です。");
        static bool Bool(JsonObject node, string name, bool fallback) => node[name] == null ? fallback : node[name] is JsonValue value && value.TryGetValue<bool>(out var result) ? result : throw new ArgumentException(name + "はtrue/falseです。");
        static int[] Ints(JsonObject node, string name) => node[name] is JsonArray array ? array.Select(value => value is JsonValue item && item.TryGetValue<int>(out var number) && number >= 0 ? number : throw new ArgumentException(name + "は0以上の整数配列です。")).ToArray() : throw new ArgumentException(name + "は配列です。");
        static long[] Longs(JsonObject node, string name) => node[name] is JsonArray array ? array.Select(value => value is JsonValue item && item.TryGetValue<long>(out var number) && number > 0 ? number : throw new ArgumentException(name + "は正のseq配列です。")).ToArray() : throw new ArgumentException(name + "は配列です。");
        static string SourceId(CompositionEditingSession session, int index) => index < session.Project.Sources.Count ? session.Project.Sources[index].Id : throw new ArgumentException("指定された元記録がありません。");
        static string RowId(CompositionEditingSession session, JsonObject operation)
        {
            if (operation["row"] == null)
                return session.SelectedRowIds.Count == 1 ? session.SelectedRowIds[0] : throw new ArgumentException("対象手順を1件指定してください。");
            int index = Int(operation, "row");
            return index < session.Project.Rows.Count ? session.Project.Rows[index].Id : throw new ArgumentException("指定された手順がありません。");
        }
        static string[] RowIds(CompositionEditingSession session, JsonObject operation, bool allowEmpty = false)
        {
            if (operation["rows"] == null && operation["row"] == null)
            {
                if (!allowEmpty && session.SelectedRowIds.Count == 0) throw new ArgumentException("対象手順を選んでください。");
                return session.SelectedRowIds.ToArray();
            }
            var indices = operation["rows"] != null ? Ints(operation, "rows") : new[] { Int(operation, "row") };
            if (!allowEmpty && indices.Length == 0) throw new ArgumentException("対象手順を選んでください。");
            if (indices.Any(index => index >= session.Project.Rows.Count)) throw new ArgumentException("指定された手順がありません。");
            return indices.Select(index => session.Project.Rows[index].Id).ToArray();
        }
        static CompositionInsertPosition InsertPosition(string value) => value == "before" ? CompositionInsertPosition.Before : value == "after" ? CompositionInsertPosition.After : value == "end" ? CompositionInsertPosition.End : throw new ArgumentException("positionはbefore/after/endです。");
        static CompositionMoveDirection MoveDirection(string value) => value == "first" ? CompositionMoveDirection.First : value == "up" ? CompositionMoveDirection.Up : value == "down" ? CompositionMoveDirection.Down : value == "last" ? CompositionMoveDirection.Last : throw new ArgumentException("directionはfirst/up/down/lastです。");
        static CompositionCloseDecision CloseDecision(string value) => value == "save" ? CompositionCloseDecision.Save : value == "discard" ? CompositionCloseDecision.Discard : value == "cancel" ? CompositionCloseDecision.Cancel : throw new ArgumentException("decisionはsave/discard/cancelです。");
    }
}
