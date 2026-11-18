using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using OpRec;

namespace OperationRecorderApp
{
    internal sealed record RecordedNarration(string Path, long StartMs)
    {
        internal const int BytesPerSecond = 32000;

        public static RecordedNarration Read(string directory)
        {
            var media = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(directory, "media.json")));
            if (media?["audio"] is not JsonObject audio ||
                audio["file"]?.GetValue<string>() != "narration.wav" ||
                audio["startMs"] is not JsonValue clock || !clock.TryGetValue<long>(out long offset) ||
                offset < 0 || offset > TimeSpan.FromDays(1).TotalMilliseconds)
                throw new InvalidDataException("記録に有効なマイク音声情報がありません。");
            return new RecordedNarration(System.IO.Path.Combine(directory, "narration.wav"), offset);
        }

        public string Regenerate(TranscriptionSettings options, long stepStartMs, long? nextStepMs,
            Func<string, CancellationToken, JsonArray> recognize = null)
        {
            using var audio = new WaveFileReader(Path);
            long start = Math.Clamp((stepStartMs - StartMs) * (BytesPerSecond / 1000), 0, audio.Length);
            long end = nextStepMs.HasValue
                ? Math.Clamp((nextStepMs.Value - StartMs) * (BytesPerSecond / 1000), 0, audio.Length) : audio.Length;
            if (end <= start) throw new InvalidOperationException("この手順に対応する録音区間がありません。");
            var result = RecognizeClip(audio, start, end - start, CancellationToken.None,
                recognize ?? ((file, token) => NarrationTranscription.Recognize(options, file, token)));
            return string.Join(Environment.NewLine, result.Select(segment => segment?["text"]?.GetValue<string>())
                .Where(text => !string.IsNullOrWhiteSpace(text)).Select(SessionTranscript.EscapeMarkdown));
        }

        internal static JsonArray RecognizeClip(WaveFileReader source, long start, long length,
            CancellationToken cancellation, Func<string, CancellationToken, JsonArray> recognize)
        {
            if (source.WaveFormat.Encoding != WaveFormatEncoding.Pcm || source.WaveFormat.SampleRate != 16000 ||
                source.WaveFormat.Channels != 1 || source.WaveFormat.BitsPerSample != 16)
                throw new InvalidDataException("OperationRecorderのマイク録音形式（16 kHz、モノラル、16ビットPCM）が必要です。");
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oprec-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                source.Position = start;
                using (var clip = new WaveFileWriter(path, source.WaveFormat))
                {
                    byte[] buffer = new byte[BytesPerSecond];
                    while (length > 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        int read = source.Read(buffer, 0, (int)Math.Min(length, buffer.Length));
                        if (read == 0) throw new EndOfStreamException("録音区間を最後まで読み取れませんでした。");
                        clip.Write(buffer, 0, read);
                        length -= read;
                    }
                }
                return recognize(path, cancellation);
            }
            finally { File.Delete(path); }
        }
    }

    internal sealed class LiveNarration
    {
        internal const int ClipBytes = 12 * RecordedNarration.BytesPerSecond;
        readonly CancellationTokenSource cancellation = new();
        readonly SemaphoreSlim signal = new(0);
        readonly Task<JsonArray> worker;
        long bytesReady;
        bool complete;
        string latest;

        public LiveNarration(TranscriptionSettings options, string file, object waveLock,
            Func<string, CancellationToken, JsonArray> recognize = null)
        {
            worker = Task.Run(() => Run(file, waveLock, cancellation.Token,
                recognize ?? ((clip, token) => NarrationTranscription.Recognize(options, clip, token))));
        }

        public string Latest => Volatile.Read(ref latest);

        public void Publish(long bytes, bool complete = false)
        {
            Interlocked.Exchange(ref bytesReady, bytes);
            Volatile.Write(ref this.complete, complete);
            signal.Release();
        }

        public void Cancel()
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void ReleaseAfterCancellation()
        {
            Cancel();
            _ = worker.ContinueWith(t =>
            {
                _ = t.Exception;
                signal.Dispose();
                cancellation.Dispose();
            }, TaskScheduler.Default);
        }

        public void Save(string directory, long audioStartMs)
        {
            try { SessionTranscript.Save(directory, worker.GetAwaiter().GetResult(), audioStartMs); }
            finally { signal.Dispose(); cancellation.Dispose(); }
        }

        async Task<JsonArray> Run(string file, object waveLock, CancellationToken token,
            Func<string, CancellationToken, JsonArray> recognize)
        {
            var result = new JsonArray();
            long start = 0;
            while (true)
            {
                await signal.WaitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                bool final = Volatile.Read(ref complete);
                long ready = Interlocked.Read(ref bytesReady);
                while (ready - start >= ClipBytes || (final && ready > start))
                {
                    long length = Math.Min(ClipBytes, ready - start);
                    using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    WaveFileReader reader;
                    lock (waveLock) reader = new WaveFileReader(input);
                    using var source = reader;
                    var segments = RecordedNarration.RecognizeClip(source, start, length, token, recognize);
                    long offset = start / (RecordedNarration.BytesPerSecond / 1000);
                    foreach (var segment in segments.OfType<JsonObject>())
                    {
                        segment["startMs"] = segment["startMs"].GetValue<long>() + offset;
                        segment["endMs"] = segment["endMs"].GetValue<long>() + offset;
                        Volatile.Write(ref latest, segment["text"].GetValue<string>());
                        result.Add(segment.DeepClone());
                    }
                    start += length;
                }
                if (final) return result;
            }
        }
    }
}
