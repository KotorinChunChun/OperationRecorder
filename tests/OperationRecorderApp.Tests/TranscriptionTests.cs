using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Xunit;

namespace OperationRecorderApp.Tests
{
    public class TranscriptionTests : IDisposable
    {
        readonly TestSession session = new TestSession();

        public void Dispose() => session.Dispose();

        [Fact]
        public void 録音だけを選べて既存のWhisper設定は引き継ぐ()
        {
            var settings = new AppSettings { RecordNarration = true };
            Assert.Equal(TranscriptionProvider.None, TranscriptionSettings.SelectedProvider(settings));
            Assert.Null(NarrationTranscription.Save(TranscriptionSettings.From(settings), session.Dir, 0));
            Assert.False(File.Exists(Path.Combine(session.Dir, "transcript.json")));
            settings.TranscriptionPython = @"C:\local\python.exe";
            settings.TranscriptionModel = @"C:\local\whisper";
            Assert.Equal(TranscriptionProvider.Whisper, TranscriptionSettings.SelectedProvider(settings));
            settings.NarrationProvider = "None";
            Assert.Equal(TranscriptionProvider.None, TranscriptionSettings.SelectedProvider(settings));
        }

        [Fact]
        public void 旧Haas設定を引き継ぎWhisperの設定と別々に保存する()
        {
            var settings = AppSettings.FromJson("""{"NarrationProvider":"Haas","TranscriptionPython":"legacy-python","TranscriptionModel":"legacy-haas"}""");
            Assert.Equal("legacy-haas", TranscriptionSettings.From(settings).LocalModel);
            settings.TranscriptionModel = "my-whisper";
            var saved = AppSettings.FromJson(settings.ToJson());
            Assert.Equal("legacy-haas", TranscriptionSettings.From(saved, TranscriptionProvider.Haas).LocalModel);
            Assert.Equal("my-whisper", TranscriptionSettings.From(saved, TranscriptionProvider.Whisper).LocalModel);
        }

        [Fact]
        public void 録音中に先行区間を処理し停止時に最後の区間を一度だけ保存する()
        {
            string wav = Path.Combine(session.Dir, "narration.wav");
            var gate = new object();
            var durations = new System.Collections.Generic.List<double>();
            var options = TranscriptionSettings.From(new AppSettings { NarrationProvider = "Whisper" });
            var live = new LiveNarration(options, wav, gate, (file, _) =>
            {
                using var audio = new WaveFileReader(file);
                durations.Add(audio.TotalTime.TotalSeconds);
                return JsonNode.Parse("""[{"startMs":0,"endMs":500,"text":"途中の本文"}]""").AsArray();
            });
            bool saveStarted = false;
            try
            {
                using (var output = new WaveFileWriter(new FileStream(wav, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite),
                    new WaveFormat(16000, 16, 1)))
                {
                    var first = new byte[LiveNarration.ClipBytes];
                    output.Write(first, 0, first.Length);
                    output.Flush();
                    live.Publish(first.Length);
                    Assert.True(SpinWait.SpinUntil(() => live.Latest == "途中の本文", 5000));
                    Assert.False(File.Exists(Path.Combine(session.Dir, "transcript.json")));
                    var last = new byte[RecordedNarration.BytesPerSecond];
                    output.Write(last, 0, last.Length);
                }
                live.Publish(LiveNarration.ClipBytes + RecordedNarration.BytesPerSecond, complete: true);
                saveStarted = true;
                live.Save(session.Dir, audioStartMs: 250);
                Assert.Equal(new[] { 12.0, 1.0 }, durations);
                var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(session.Dir, "transcript.json"))).AsArray();
                Assert.Equal(new long[] { 250, 12250 }, saved.Select(segment => segment["startMs"].GetValue<long>()));
            }
            finally { if (!saveStarted) live.ReleaseAfterCancellation(); }
        }

        [Fact]
        public void 本文再生成は選択した手順の範囲だけを読み取る()
        {
            var input = new RecordedNarration(MakeWave(4), StartMs: 1000);
            var options = TranscriptionSettings.From(new AppSettings { NarrationProvider = "Whisper" });
            var text = input.Regenerate(options, stepStartMs: 2000, nextStepMs: 3250, (file, _) =>
            {
                using var clip = new WaveFileReader(file);
                Assert.Equal(1.25, clip.TotalTime.TotalSeconds);
                return JsonNode.Parse("""[{"startMs":0,"endMs":500,"text":"新しい<本文>"}]""").AsArray();
            });
            Assert.Equal(@"新しい\<本文\>", text);
            Assert.False(File.Exists(Path.Combine(session.Dir, "transcript.json")));
        }

        [Fact]
        public void モデル削除は管理用フォルダーの読み取り専用ファイルだけを削除する()
        {
            var installer = new LocalTranscriptionInstaller(session.Root);
            var paths = installer.Paths(TranscriptionProvider.Whisper);
            string managed = Path.GetDirectoryName(paths.Model);
            Directory.CreateDirectory(paths.Model);
            string local = Path.Combine(paths.Model, "model.bin");
            File.WriteAllText(local, "試験データ");
            Assert.Throws<IOException>(() => installer.Remove(TranscriptionProvider.Whisper));
            Assert.True(File.Exists(local));
            string external = Path.Combine(session.Root, "手動のモデル.bin");
            File.WriteAllText(external, "手動モデル");
            File.WriteAllText(Path.Combine(managed, ".operation-recorder-model"), "Whisper");
            File.SetAttributes(local, FileAttributes.ReadOnly);
            installer.Remove(TranscriptionProvider.Whisper);
            Assert.False(Directory.Exists(managed));
            Assert.Equal("手動モデル", File.ReadAllText(external));
        }

        [Fact]
        public void キーを暗号化してサービスと接続先ごとに保管する()
        {
            const string dummy = "dummy-test-key-that-must-not-be-stored-in-plain-text";
            var credentials = new TranscriptionCredentials(session.Root);
            var first = TranscriptionSettings.ServerEndpoint("http://127.0.0.1:8080/v1");
            var matching = TranscriptionSettings.ServerEndpoint("http://127.0.0.1:8080/v1/audio/transcriptions");
            var second = TranscriptionSettings.ServerEndpoint("http://127.0.0.1:8081");
            credentials.Save(TranscriptionProvider.Compatible, first, dummy);
            Assert.Equal(dummy, credentials.Read(TranscriptionProvider.Compatible, matching));
            Assert.Null(credentials.Read(TranscriptionProvider.Compatible, second));
            Assert.Null(credentials.Read(TranscriptionProvider.OpenAI, null));
            var protectedFile = Assert.Single(Directory.GetFiles(Path.Combine(session.Root, "credentials")));
            Assert.DoesNotContain(dummy, Encoding.UTF8.GetString(File.ReadAllBytes(protectedFile)));
            Assert.DoesNotContain(dummy, JsonSerializer.Serialize(new AppSettings()));
            credentials.Remove(TranscriptionProvider.Compatible, matching);
            Assert.False(credentials.Exists(TranscriptionProvider.Compatible, first));
        }

        [Theory]
        [InlineData("http://asr.example.test/v1")]
        [InlineData("https://asr.example.test/?key=secret")]
        [InlineData("https://name:password@asr.example.test/")]
        public void 暗号化されない外部接続と資格情報付きURLを拒否する(string address)
        {
            Assert.Throws<ArgumentException>(() => TranscriptionSettings.ServerEndpoint(address));
        }

        [Fact]
        public async Task 互換サーバーのモデルを省略し分割後の時刻を戻す()
        {
            var settings = TranscriptionSettings.From(new AppSettings { NarrationProvider = "Compatible", TranscriptionServerUrl = "http://127.0.0.1:8080" });
            int requests = 0;
            using var client = new HttpClient(new Stub(async request =>
            {
                int chunk = ++requests;
                Assert.Equal("/v1/audio/transcriptions", request.RequestUri.AbsolutePath);
                Assert.Equal("Bearer test-token", request.Headers.Authorization.ToString());
                var form = Assert.IsType<MultipartFormDataContent>(request.Content);
                var fields = form.ToDictionary(part => part.Headers.ContentDisposition.Name.Trim('"'));
                Assert.False(fields.ContainsKey("model"));
                Assert.Equal("verbose_json", await fields["response_format"].ReadAsStringAsync());
                using var wave = new WaveFileReader(await fields["file"].ReadAsStreamAsync());
                Assert.Equal(chunk == 1 ? 20 : 1, wave.TotalTime.TotalSeconds);
                return Json("""{"segments":[{"start":0.1,"end":0.4,"text":"試験"}]}""");
            }));
            var segments = await TranscriptionApi.Run(settings, "test-token", MakeWave(21), client);
            Assert.Equal(2, requests);
            Assert.Equal(2, OpRec.SessionTranscript.Save(session.Dir, segments, 500));
            var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(session.Dir, "transcript.json")));
            Assert.Equal(600, saved[0]["startMs"].GetValue<int>());
            Assert.Equal(20600, saved[1]["startMs"].GetValue<int>());
        }

        [Fact]
        public async Task OpenAIに時刻付きGPT形式を指定する()
        {
            var settings = TranscriptionSettings.From(new AppSettings { NarrationProvider = "OpenAI" });
            using var client = new HttpClient(new Stub(async request =>
            {
                Assert.Equal("https://api.openai.com/v1/audio/transcriptions", request.RequestUri.AbsoluteUri);
                Assert.Equal("Bearer test-token", request.Headers.Authorization.ToString());
                var fields = ((MultipartFormDataContent)request.Content).ToDictionary(part => part.Headers.ContentDisposition.Name.Trim('"'));
                Assert.Equal("gpt-4o-transcribe-diarize", await fields["model"].ReadAsStringAsync());
                Assert.Equal("diarized_json", await fields["response_format"].ReadAsStringAsync());
                Assert.Equal("auto", await fields["chunking_strategy"].ReadAsStringAsync());
                return Json("""{"segments":[{"start":0.01,"end":0.9,"text":"OpenAI"}]}""");
            }));
            var segments = await TranscriptionApi.Run(settings, "test-token", MakeWave(1), client);
            Assert.Equal(10L, Assert.Single(segments)["startMs"].GetValue<long>());
        }

        [Fact]
        public async Task Geminiの音声と構造化出力を指定し時刻を読む()
        {
            var settings = TranscriptionSettings.From(new AppSettings { NarrationProvider = "Gemini" });
            using var client = new HttpClient(new Stub(async request =>
            {
                Assert.Equal("https://generativelanguage.googleapis.com", request.RequestUri.GetLeftPart(UriPartial.Authority));
                Assert.EndsWith(":generateContent", request.RequestUri.AbsolutePath);
                Assert.Empty(request.RequestUri.Query);
                Assert.Equal("test-token", Assert.Single(request.Headers.GetValues("x-goog-api-key")));
                var body = JsonNode.Parse(await request.Content.ReadAsStringAsync());
                Assert.Equal("audio/wav", body["contents"][0]["parts"][1]["inlineData"]["mimeType"].GetValue<string>());
                Assert.Equal("application/json", body["generationConfig"]["responseMimeType"].GetValue<string>());
                return Json("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"[{\"startMs\":123,\"endMs\":456,\"text\":\"Gemini\"}]"}]}}]}""");
            }));
            var segments = await TranscriptionApi.Run(settings, "test-token", MakeWave(1), client);
            Assert.Equal(123L, Assert.Single(segments)["startMs"].GetValue<long>());
        }

        [Fact]
        public async Task APIの失敗にキーや応答本文を含めない()
        {
            var settings = TranscriptionSettings.From(new AppSettings { NarrationProvider = "OpenAI" });
            using var client = new HttpClient(new Stub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            { Content = new StringContent("secret-body-test-token") })));
            var error = await Assert.ThrowsAsync<IOException>(() => TranscriptionApi.Run(settings, "test-token", MakeWave(1), client));
            Assert.Contains("HTTP 401", error.Message);
            Assert.DoesNotContain("secret-body-test-token", error.Message);
        }

        [Fact]
        public async Task exeでキー未登録なら既存の文字起こしを残す()
        {
            MakeWave(1);
            string settingsDir = Path.Combine(session.Root, "settings");
            Directory.CreateDirectory(settingsDir);
            File.WriteAllText(Path.Combine(settingsDir, "settings.json"), """{"NarrationProvider":"OpenAI"}""");
            File.WriteAllText(Path.Combine(session.Dir, "media.json"), """{"audio":{"file":"narration.wav","startMs":500}}""");
            string existing = Path.Combine(session.Dir, "transcript.json");
            File.WriteAllText(existing, "[]");
            string exe = Path.Combine(AppContext.BaseDirectory, "OperationRecorder.exe");
            var launch = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true,
                RedirectStandardOutput = true, StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true };
            foreach (string argument in new[] { "--transcribe", session.Dir, "--settings-dir", settingsDir }) launch.ArgumentList.Add(argument);
            using var process = Process.Start(launch);
            var output = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(1, process.ExitCode);
            Assert.Contains("APIキー", await output);
            Assert.Equal("[]", File.ReadAllText(existing));
        }

        string MakeWave(int seconds)
        {
            string path = Path.Combine(session.Dir, "narration.wav");
            using var wave = new WaveFileWriter(path, new WaveFormat(16000, 16, 1));
            byte[] samples = new byte[32000 * seconds];
            wave.Write(samples, 0, samples.Length);
            return path;
        }

        static HttpResponseMessage Json(string json) => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
        }
    }
}
