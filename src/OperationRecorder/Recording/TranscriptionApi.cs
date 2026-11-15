using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using OpRec;

namespace OperationRecorderApp
{
    static class TranscriptionApi
    {
        const string GeminiPrompt = "Transcribe the spoken words in their original language without summarizing or adding speech. " +
            "Return chronological segments with text and startMs/endMs measured in milliseconds from the beginning of this clip. " +
            "Break at natural pauses. Return an empty array for silence.";

        internal static async Task<JsonArray> Run(TranscriptionSettings options, TranscriptionCredentials credentials, string audio,
            CancellationToken cancellation = default)
        {
            string key = credentials.Read(options.Provider, options.Endpoint);
            if (key == null && options.Provider != TranscriptionProvider.Compatible)
                throw new InvalidOperationException("「記録対象」で選択中のサービスのAPIキーを保存してください。");
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            return await Run(options, key, audio, client, cancellation).ConfigureAwait(false);
        }

        internal static async Task<JsonArray> Run(TranscriptionSettings options, string key, string audio, HttpClient client,
            CancellationToken cancellation = default)
        {
            bool gemini = options.Provider == TranscriptionProvider.Gemini;
            bool compatible = options.Provider == TranscriptionProvider.Compatible;
            bool native = compatible && options.Endpoint.AbsolutePath.EndsWith("/inference", StringComparison.Ordinal);
            bool diarize = !gemini && !compatible && options.Model.StartsWith("gpt-4o-transcribe-diarize", StringComparison.Ordinal);
            bool timed = compatible || diarize || options.Model == "whisper-1";
            Uri endpoint = options.Provider switch
            {
                TranscriptionProvider.Gemini => GeminiEndpoint(options.Model),
                TranscriptionProvider.OpenAI => new Uri("https://api.openai.com/v1/audio/transcriptions"),
                _ => options.Endpoint,
            };

            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timer.CancelAfter(TimeSpan.FromMinutes(30));
            var results = new JsonArray();
            long totalTextBytes = 0;
            int secondsPerChunk = gemini ? 60 : compatible || !timed ? 20 : 180;
            try
            {
                foreach (var chunk in ReadChunks(audio, secondsPerChunk))
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                    if (gemini)
                    {
                        request.Headers.Add("x-goog-api-key", key);
                        request.Content = GeminiContent(chunk.Wav);
                    }
                    else
                    {
                        if (key != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                        string format = diarize ? "diarized_json" : timed ? "verbose_json" : "json";
                        request.Content = MultipartContent(chunk.Wav, native ? null : options.Model, format, diarize, native);
                    }
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timer.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        throw new IOException($"文字起こしサービスから HTTP {(int)response.StatusCode} が返りました。接続先・モデル・APIキーを確認してください。");
                    if (compatible && response.Headers.TryGetValues("X-Speech-Stop", out var stops) &&
                        System.Linq.Enumerable.Contains(stops, "model_limit"))
                        throw new InvalidDataException("互換サーバーのモデルが出力上限に達したため、文字起こしを保存できません。");

                    byte[] body = await ReadResponse(response.Content, timer.Token).ConfigureAwait(false);
                    JsonNode result;
                    if (compatible && response.Content.Headers.ContentType?.MediaType == "text/plain")
                        result = new JsonObject { ["text"] = System.Text.Encoding.UTF8.GetString(body) };
                    else
                        result = JsonNode.Parse(body);
                    foreach (var segment in ReadSegments(result, gemini, chunk.DurationMs))
                    {
                        totalTextBytes += System.Text.Encoding.UTF8.GetByteCount(segment.Text) + 96;
                        if (totalTextBytes > SessionTranscript.MaximumBytes) throw new InvalidDataException("文字起こし結果が大きすぎます。");
                        results.Add(new JsonObject
                        {
                            ["startMs"] = chunk.OffsetMs + segment.StartMs,
                            ["endMs"] = chunk.OffsetMs + segment.EndMs,
                            ["text"] = segment.Text,
                        });
                    }
                }
                return results;
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            { throw new TimeoutException("文字起こしサービスが30分以内に応答しませんでした。"); }
            catch (HttpRequestException) { throw new IOException("文字起こしサービスに接続できません。通信設定を確認してください。"); }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            {
                throw new InvalidDataException("文字起こしサービスから有効な応答を受け取れませんでした。");
            }
        }

        static Uri GeminiEndpoint(string model)
        {
            if (!Regex.IsMatch(model, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z"))
                throw new ArgumentException("GeminiのモデルIDを確認してください。");
            return new Uri($"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
        }

        static HttpContent MultipartContent(byte[] wav, string model, string format, bool diarize, bool native)
        {
            var file = new ByteArrayContent(wav);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            var form = new MultipartFormDataContent { { file, "file", "narration.wav" }, { new StringContent(format), "response_format" } };
            if (model != null) form.Add(new StringContent(model), "model");
            if (diarize) form.Add(new StringContent("auto"), "chunking_strategy");
            if (native) form.Add(new StringContent("auto"), "language");
            return form;
        }

        static HttpContent GeminiContent(byte[] wav) => JsonContent.Create(new
        {
            contents = new[] { new { parts = new object[]
            {
                new { text = GeminiPrompt },
                new { inlineData = new { mimeType = "audio/wav", data = Convert.ToBase64String(wav) } },
            } } },
            generationConfig = new
            {
                responseMimeType = "application/json",
                responseSchema = new
                {
                    type = "ARRAY",
                    items = new
                    {
                        type = "OBJECT",
                        properties = new
                        {
                            startMs = new { type = "INTEGER" },
                            endMs = new { type = "INTEGER" },
                            text = new { type = "STRING" },
                        },
                        required = new[] { "startMs", "endMs", "text" },
                    },
                },
            },
        });

        static async Task<byte[]> ReadResponse(HttpContent content, CancellationToken token)
        {
            using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] data = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(data, token).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + read > SessionTranscript.MaximumBytes) throw new InvalidDataException("文字起こし応答が大きすぎます。");
                buffer.Write(data, 0, read);
            }
            return buffer.ToArray();
        }

        static IEnumerable<(long StartMs, long EndMs, string Text)> ReadSegments(JsonNode result, bool gemini, long durationMs)
        {
            JsonArray segments;
            if (gemini)
            {
                if (result?["candidates"] is not JsonArray candidates || candidates.Count == 0 ||
                    candidates[0]?["finishReason"]?.GetValue<string>() != "STOP" ||
                    candidates[0]?["content"]?["parts"] is not JsonArray parts)
                    throw new InvalidDataException("Geminiの文字起こしが完了しませんでした。");
                var json = new System.Text.StringBuilder();
                foreach (var part in parts)
                    if (part?["thought"]?.GetValue<bool>() != true && part?["text"] is JsonValue text)
                        json.Append(text.GetValue<string>());
                segments = JsonNode.Parse(json.ToString()).AsArray();
            }
            else if (result is JsonObject obj && obj["segments"] is JsonArray timed)
                segments = timed;
            else if (result is JsonObject untimed && untimed["text"] is JsonValue value && value.TryGetValue<string>(out var text))
            {
                if (!string.IsNullOrWhiteSpace(text)) yield return (0, durationMs, text);
                yield break;
            }
            else throw new InvalidDataException("文字起こし応答に本文がありません。");

            foreach (var segment in segments)
            {
                if (segment is not JsonObject entry || entry["text"] is not JsonValue caption ||
                    !caption.TryGetValue<string>(out string words))
                    throw new InvalidDataException("文字起こし応答の区間に本文がありません。");
                if (string.IsNullOrWhiteSpace(words)) continue;
                long start = ReadClock(entry, gemini ? "startMs" : "start", gemini ? 1 : 1000, durationMs);
                long end = ReadClock(entry, gemini ? "endMs" : "end", gemini ? 1 : 1000, durationMs);
                if (end <= start) throw new InvalidDataException("文字起こし応答の区間時刻が音声の長さと一致しません。");
                yield return (start, end, words);
            }
        }

        static long ReadClock(JsonObject segment, string name, int scale, long durationMs)
        {
            if (segment[name] is not JsonValue value || !value.TryGetValue<double>(out double time) || !double.IsFinite(time))
                throw new InvalidDataException("文字起こし応答の区間時刻が無効です。");
            return (long)Math.Clamp(Math.Round(time * scale), 0, durationMs);
        }

        static IEnumerable<(byte[] Wav, long OffsetMs, long DurationMs)> ReadChunks(string path, int seconds)
        {
            using var source = new WaveFileReader(path);
            var format = source.WaveFormat;
            if (format.Encoding != WaveFormatEncoding.Pcm || format.SampleRate != 16000 ||
                format.Channels != 1 || format.BitsPerSample != 16 || source.Length == 0 ||
                source.TotalTime > TimeSpan.FromDays(1))
                throw new InvalidDataException("OperationRecorderでマイク録音した有効なWAVが必要です。");
            byte[] buffer = new byte[format.AverageBytesPerSecond * seconds];
            long offset = 0;
            int read;
            while ((read = source.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false)) != 0)
            {
                using var output = new MemoryStream();
                using (var wav = new WaveFileWriter(output, format)) wav.Write(buffer, 0, read);
                long duration = (read * 1000L + format.AverageBytesPerSecond - 1) / format.AverageBytesPerSecond;
                yield return (output.ToArray(), offset, duration);
                offset += duration;
            }
        }
    }
}
