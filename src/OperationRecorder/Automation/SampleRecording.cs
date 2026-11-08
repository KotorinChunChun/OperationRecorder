using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using OpRec;

namespace OperationRecorderApp
{
    internal static class SampleRecording
    {
        const long MaximumDurationMs = 7L * 24 * 60 * 60 * 1000;
        const long MaximumImagesBytes = 256L * 1024 * 1024;

        public static JsonObject Complete(JsonObject request, AutomationWorkspace workspace)
        {
            var sample = workspace.Resolve(BasicAutomation.Required(request, "sample"), true);
            var destination = workspace.Resolve(BasicAutomation.Required(request, "session"));
            var records = workspace.Resolve("records");
            if (!Directory.Exists(sample)) throw new ArgumentException("sampleはフォルダーです。");
            if (!string.Equals(Path.GetDirectoryName(destination), records, StringComparison.OrdinalIgnoreCase)
                || AutomationWorkspace.Within(destination, sample) || AutomationWorkspace.Within(sample, destination))
                throw new ArgumentException("sessionはサンプルと別のrecords直下のフォルダーを指定してください。");
            if (Path.Exists(destination)) throw new IOException("指定した記録はすでに存在します。");

            var events = CompositionProject.ReadEvents(sample);
            long duration = 0;
            var images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in events)
            {
                if (record["seq"] is not JsonValue number || !number.TryGetValue<int>(out var seq) || seq <= 0)
                    throw new ArgumentException("seqは正の32ビット整数です。");
                if (record["tMs"] is not JsonValue time || !time.TryGetValue<long>(out var elapsed) || elapsed < 0 || elapsed > MaximumDurationMs)
                    throw new ArgumentException("tMsは0から7日以内の整数ミリ秒です。");
                BasicAutomation.Required(record, "kind");
                duration = Math.Max(duration, elapsed);

                foreach (var side in new[] { "before", "after" })
                {
                    if (record[side] == null) continue;
                    var image = BasicAutomation.ResolveImage(record[side], sample, workspace);
                    if (!images.TryGetValue(image, out var relative))
                    {
                        relative = "img/" + (images.Count + 1).ToString("D4") + ".png";
                        images.Add(image, relative);
                    }
                    record[side]["file"] = relative;
                }
            }

            var staging = workspace.Resolve(Path.Combine(records, ".sample-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(staging);
            try
            {
                Directory.CreateDirectory(Path.Combine(staging, "img"));
                long imageBytes = 0;
                foreach (var image in images)
                {
                    using var source = File.OpenRead(image.Key);
                    if (source.Length > MaximumImagesBytes - imageBytes)
                        throw new ArgumentException("サンプル画像の合計は256 MiBまでです。");
                    imageBytes += source.Length;
                    var file = Path.Combine(staging, image.Value);
                    using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        source.CopyTo(output);
                    CompositionProject.CheckImage(file, requirePng: true);
                }
                var recorder = new Recorder
                {
                    Caption = BasicAutomation.Text(request, "caption")?.Trim(),
                    Description = BasicAutomation.Text(request, "description")?.Trim(),
                };
                recorder.CompleteSample(staging, events, duration);
                var metadata = BasicAutomation.ReadObject(Path.Combine(staging, "session.json"));
                ReportGenerator.ListSteps(staging);
                Directory.Move(staging, destination);
                return new JsonObject { ["directory"] = destination, ["eventCount"] = recorder.SavedCount, ["end"] = metadata["end"].DeepClone() };
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
            }
        }
    }
}
