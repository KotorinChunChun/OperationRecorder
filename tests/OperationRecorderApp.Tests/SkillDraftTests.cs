using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using OpRec;
using Xunit;

namespace OperationRecorderApp.Tests;

public sealed class SkillDraftTests : IDisposable
{
    readonly TestSession session = new();

    [Fact]
    public async Task 承認済みの文章とマスク画像だけ送信し編集案からスキルと手順書を作成する()
    {
        string eventsPath = Path.Combine(session.Dir, "events.jsonl");
        string beforePath = session.ImagePath(1, "before");
        byte[] originalEvents = File.ReadAllBytes(eventsPath);
        byte[] originalImage = File.ReadAllBytes(beforePath);
        ReportGenerator.SaveStepEdit(session.Dir, 1, "手修正した操作", "伏せたい設定値");
        byte[] originalEdits = File.ReadAllBytes(Path.Combine(session.Dir, "edits.json"));
        File.WriteAllText(Path.Combine(session.Dir, "transcript.json"),
            """[{"startMs":1001,"endMs":1300,"text":"録音の説明"}]""");
        var source = new SkillSource(session.Dir);
        var choices = source.SelectSteps();
        Assert.Contains("伏せたい設定値", choices[0].Action);
        Assert.Contains("録音の説明", choices[0].Narration);
        Assert.All(choices, item => Assert.False(item.IncludeBefore || item.IncludeAfter));
        foreach (var item in choices.Skip(1)) item.Include = false;
        choices[0].Action = "登録ボタンを押す";
        choices[0].Narration = "";
        choices[0].IncludeBefore = true;
        choices[0].BeforeMasks.Add(new SkillMask(0, 0, 30, 20));
        var review = source.Prepare(choices);

        using var http = new HttpClient(new Handler(async request =>
        {
            string json = await request.Content.ReadAsStringAsync();
            Assert.DoesNotContain("伏せたい設定値", json);
            Assert.DoesNotContain("録音の説明", json);
            Assert.DoesNotContain("閉じる", json);
            Assert.DoesNotContain("HANBAI.EXE", json);
            Assert.DoesNotContain("dummy-key", json);
            Assert.Equal("Bearer dummy-key", request.Headers.Authorization.ToString());
            var content = JsonNode.Parse(json)["messages"][1]["content"].AsArray();
            Assert.Equal(3, content.Count);
            Assert.Equal("登録ボタンを押す", JsonNode.Parse((string)content[0]["text"])["action"].GetValue<string>());
            var data = (string)content[2]["image_url"]["url"];
            using var imageStream = new MemoryStream(Convert.FromBase64String(data.Split(',')[1]));
            using var image = new Bitmap(imageStream);
            Assert.Equal(Color.Black.ToArgb(), image.GetPixel(2, 2).ToArgb());
            Assert.NotEqual(Color.Black.ToArgb(), image.GetPixel(60, 30).ToArgb());
            return Reply(1);
        }));
        var draft = await SkillAnalysis.Generate(review, SkillAnalysis.Endpoint("https://example.test/v1"), "test-model",
            "dummy-key", http, CancellationToken.None);
        string skill = SkillDrafts.RenderSkill(draft, "register-record");
        Assert.StartsWith("---\nname: register-record\n", skill);
        Assert.DoesNotContain("base64", skill);
        string manual = HtmlExporter.ToHtml(SkillDrafts.RenderManual(draft));
        Assert.Contains("data:image/png;base64,", manual);
        Assert.Contains("根拠の操作 1", manual);
        Assert.Contains("登録する", manual);
        Assert.Equal(originalEvents, File.ReadAllBytes(eventsPath));
        Assert.Equal(originalImage, File.ReadAllBytes(beforePath));
        Assert.Equal(originalEdits, File.ReadAllBytes(Path.Combine(session.Dir, "edits.json")));
    }

    [Fact]
    public async Task 送信対象にない操作を根拠とする応答を拒否する()
    {
        var source = new SkillSource(session.Dir);
        var review = source.Prepare(source.SelectSteps().Take(1));
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Reply(999))));
        await Assert.ThrowsAsync<ArgumentException>(() => SkillAnalysis.Generate(review, SkillAnalysis.Endpoint("http://127.0.0.1:3000"),
            "test-model", "", http, CancellationToken.None));
    }

    [Fact]
    public void 拡大縮小した表示でマスクの座標を元画像へ変換する()
    {
        Assert.True(SkillDraftDialog.TryMask(new Size(400, 300), new Size(800, 400), new Rectangle(25, 40, 100, 60), out var result));
        Assert.Equal(new SkillMask(50, 0, 200, 100), result);
        Assert.False(SkillDraftDialog.TryMask(new Size(400, 300), new Size(800, 400), new Rectangle(20, 10, 100, 20), out _));
    }

    [Fact]
    public void 古い記録の画像が無くても文章だけを分析に選べる()
    {
        File.Delete(session.ImagePath(1, "before"));
        var source = new SkillSource(session.Dir);
        var choice = source.SelectSteps().First();
        Assert.Empty(Assert.Single(source.Prepare([choice]).Steps).Images);
        choice.IncludeBefore = true;
        Assert.Throws<ArgumentException>(() => source.Prepare([choice]));
    }

    static HttpResponseMessage Reply(long seq)
    {
        string plan = JsonSerializer.Serialize(new SkillProcedure("登録する", "登録を依頼された時", "アプリを開く", "",
            "登録の完了を確認する", [new SkillInstruction("登録ボタンを押す", [seq])]), SkillDraftFiles.Json);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = plan } } } })),
        };
    }

    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    public void Dispose() => session.Dispose();
}
