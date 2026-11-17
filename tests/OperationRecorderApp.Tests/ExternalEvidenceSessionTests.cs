using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

public sealed class ExternalEvidenceSessionTests
{
    public sealed class Validator : IEvidenceTargetValidator
    {
        public bool Valid = true;
        public bool Matches(EvidenceTarget target) => Valid;
    }
    public sealed class Backend : IEvidenceBackend
    {
        public int Calls;
        public Func<CaptureWork, CancellationToken, Task<string>> Handler;
        public Task<string> CaptureAndSaveAsync(CaptureWork work, CancellationToken token)
        {
            Calls++;
            if (Handler != null) return Handler(work, token);
            return Task.FromResult(EvidenceFiles.Save(work, EvidenceFiles.OpaquePng(new Size(60,40)), new EvidenceUi(Array.Empty<EvidenceNode>(), false), DateTimeOffset.UtcNow));
        }
    }
    public sealed class Context : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "oprec-evidence-test-" + Guid.NewGuid().ToString("N"));
        public readonly Guid Id = Guid.NewGuid();
        public readonly Backend Backend = new();
        public readonly Validator Validator = new();
        public readonly EvidenceSession Session;
        public Context() { Directory.CreateDirectory(Root); Session = new(Backend, Validator); }
        public EvidenceRequest Request(string method, Guid step = default, Guid token = default) => new()
        { RequestId = Guid.NewGuid(), SessionId = Id, Method = method, DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(5), StepId = step, StepToken = token, OperationName = "invoke", Outcome = "success" };
        public Task<EvidenceReply> Start() => Session.HandleAsync(Request("StartSession") with
        { Target = new(1, DateTimeOffset.UtcNow, "C:/fixture.exe", 1, 2), OutputDirectory = Path.Combine(Root, "output") });
        public void Dispose() { Session.Dispose(); Directory.Delete(Root, true); }
    }

    [Fact]
    public async Task 保存完了後だけ操作許可し前後証拠と工程を独立確定する()
    {
        using var c = new Context(); Assert.Equal("started", (await c.Start()).Status);
        var begin = await c.Session.HandleAsync(c.Request("BeginStep", Guid.NewGuid()));
        Assert.True(begin.OperationAllowed); Assert.Equal("before-saved", begin.Status);
        Assert.NotNull(begin.SavedAt); Assert.All(begin.EvidencePaths, p => Assert.True(File.Exists(p)));
        var commit = JsonSerializer.Deserialize<EvidenceCommit>(File.ReadAllText(begin.EvidencePaths[0]), EvidenceProtocol.Json);
        Assert.Equal(begin.StepId, commit.StepId); Assert.Equal(begin.StepToken, commit.StepToken);
        Assert.True(EvidenceProtocol.IsHash(commit.ImplementationSha256)); Assert.Equal("before", commit.Phase);
        var end = await c.Session.HandleAsync(c.Request("EndStep", token: begin.StepToken));
        Assert.Equal("after-saved", end.Status); Assert.False(end.OperationAllowed); Assert.Empty(end.PendingSteps);
        Assert.True(File.Exists(begin.EvidencePaths[0]));
        Assert.Equal("after", JsonSerializer.Deserialize<EvidenceCommit>(File.ReadAllText(end.EvidencePaths[0]), EvidenceProtocol.Json).Phase);
        var flush = await c.Session.HandleAsync(c.Request("Flush")); Assert.Equal("flushed", flush.Status); Assert.Empty(flush.PendingSteps);
        Assert.Single(File.ReadAllLines(flush.EvidencePaths.Single(p => p.EndsWith("events.jsonl"))));
        Assert.Empty(Directory.GetFiles(c.Root, "*.mp4", SearchOption.AllDirectories));
    }
    [Theory]
    [InlineData("capture-unavailable")]
    [InlineData("save-unavailable")]
    [InlineData("video-unavailable")]
    [InlineData("deadline-exceeded")]
    public async Task before失敗やタイムアウトなら模擬操作を一度も実行しない(string code)
    {
        using var c = new Context(); await c.Start();
        c.Backend.Handler = (w, t) => code == "deadline-exceeded" ? new TaskCompletionSource<string>().Task : throw new EvidenceUnavailableException(code);
        var request = c.Request("BeginStep", Guid.NewGuid()) with { DeadlineUtc = DateTimeOffset.UtcNow.AddMilliseconds(150) };
        int operations = 0; var response = await c.Session.HandleAsync(request);
        if (response.OperationAllowed) operations++;
        Assert.Equal(0, operations); Assert.Equal(code, response.Status); Assert.Null(response.SavedAt);
        Assert.Contains(request.StepId, response.PendingSteps);
        Assert.Equal("duplicate-step", (await c.Session.HandleAsync(request with { RequestId = Guid.NewGuid(), DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(2) })).Status);
        Assert.Equal(1, c.Backend.Calls);
    }
    [Fact]
    public async Task 重複した要求や工程は撮影を再実行せず操作も再許可しない()
    {
        using var c = new Context(); await c.Start(); var request = c.Request("BeginStep", Guid.NewGuid());
        var first = await c.Session.HandleAsync(request); Assert.True(first.OperationAllowed);
        Assert.Equal("duplicate-request", (await c.Session.HandleAsync(request)).Status);
        Assert.Equal("request-conflict", (await c.Session.HandleAsync(request with { OperationName = "custom" })).Status);
        var duplicate = await c.Session.HandleAsync(request with { RequestId = Guid.NewGuid() });
        Assert.Equal("duplicate-step", duplicate.Status); Assert.False(duplicate.OperationAllowed); Assert.Equal(first.StepToken, duplicate.StepToken);
        Assert.Equal(1, c.Backend.Calls);
        var end = c.Request("EndStep", token: first.StepToken); await c.Session.HandleAsync(end);
        Assert.Equal("duplicate-step", (await c.Session.HandleAsync(end with { RequestId = Guid.NewGuid() })).Status);
        Assert.Equal(2, c.Backend.Calls);
    }
    [Fact]
    public async Task after途中障害でもbeforeを保持し成否不明を明示する()
    {
        using var c = new Context(); await c.Start(); var begin = await c.Session.HandleAsync(c.Request("BeginStep", Guid.NewGuid()));
        c.Backend.Handler = (w,t) => throw new IOException("DUMMY_SECRET_IN_EXCEPTION");
        var end = await c.Session.HandleAsync(c.Request("EndStep", token: begin.StepToken));
        Assert.Equal("save-unavailable", end.Status); Assert.True(File.Exists(begin.EvidencePaths[0]));
        Assert.DoesNotContain("DUMMY_SECRET", JsonSerializer.Serialize(end));
        var retry = await c.Session.HandleAsync(c.Request("EndStep", token: begin.StepToken)); Assert.Equal("step-indeterminate", retry.Status);
        var stop = await c.Session.HandleAsync(c.Request("StopSession")); Assert.Contains(begin.StepId, stop.PendingSteps);
        Assert.False(stop.OperationAllowed);
        var state = JsonSerializer.Deserialize<EvidenceStep>(File.ReadAllText(Directory.GetFiles(c.Root, "step.json", SearchOption.AllDirectories).Single()), EvidenceProtocol.Json);
        Assert.Equal("after-unconfirmed", state.Status);
    }
    [Fact]
    public async Task 不明トークンや変更対象を拒否し失敗証拠にも同じ工程IDを持たせる()
    {
        using var c = new Context(); await c.Start(); Assert.Equal("unknown-step", (await c.Session.HandleAsync(c.Request("EndStep", token: Guid.NewGuid()))).Status);
        var begin = await c.Session.HandleAsync(c.Request("BeginStep", Guid.NewGuid()));
        var failure = await c.Session.HandleAsync(c.Request("RecordFailure", begin.StepId) with { ErrorCode = "unexpected-state" });
        Assert.Equal("failure-saved", failure.Status); Assert.Equal(begin.StepId, failure.StepId); Assert.False(failure.OperationAllowed);
        c.Validator.Valid = false;
        Assert.Equal("target-mismatch", (await c.Session.HandleAsync(c.Request("EndStep", token: begin.StepToken))).Status);
        Assert.Equal(2, c.Backend.Calls);
    }
    [Fact]
    public async Task ファイル改変や保存完了マーカー欠落を成功扱いしない()
    {
        using var c = new Context(); await c.Start();
        c.Backend.Handler = (w,t) => {
            string path = EvidenceFiles.Save(w, EvidenceFiles.OpaquePng(new Size(10,10)), new EvidenceUi(Array.Empty<EvidenceNode>(), false), DateTimeOffset.UtcNow);
            File.AppendAllText(Path.Combine(w.Directory, "ui.json"), "tamper"); return Task.FromResult(path);
        };
        var result = await c.Session.HandleAsync(c.Request("BeginStep", Guid.NewGuid())); Assert.Equal("save-unconfirmed", result.Status); Assert.False(result.OperationAllowed);
    }
    [Fact]
    public async Task 自由文字メタデータや未知の設定を受理しない()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EvidenceRequest>("{\"password\":\"DUMMY_SECRET\"}", EvidenceProtocol.Json));
        using var c = new Context(); await c.Start();
        Assert.Equal("invalid-metadata", (await c.Session.HandleAsync(c.Request("BeginStep", Guid.NewGuid()) with { ExpectedStateMetadata = new(FingerprintSha256: "DUMMY_SECRET") })).Status);
        Assert.Equal("invalid-request", (await c.Session.HandleAsync(c.Request("BeginStep", Guid.NewGuid()) with { OperationName = "DUMMY_SECRET" })).Status);
        Assert.Equal(0, c.Backend.Calls);
    }
    [Fact]
    public async Task 要求数の上限でも開始済み工程の確定とFlushとStopを妨げない()
    {
        using var c = new Context(); using var capped = new EvidenceSession(c.Backend,c.Validator,maxRequests:2);
        Assert.Equal("started",(await capped.HandleAsync(c.Request("StartSession") with {Target=new(1,DateTimeOffset.UtcNow,"C:/fixture.exe",1,2),OutputDirectory=Path.Combine(c.Root,"capped")})).Status);
        var begin=await capped.HandleAsync(c.Request("BeginStep",Guid.NewGuid())); Assert.True(begin.OperationAllowed);
        Assert.Equal("after-saved",(await capped.HandleAsync(c.Request("EndStep",token:begin.StepToken))).Status);
        var limited=await capped.HandleAsync(c.Request("BeginStep",Guid.NewGuid())); Assert.Equal("session-limit",limited.Status);Assert.False(limited.OperationAllowed);
        Assert.Equal("flushed",(await capped.HandleAsync(c.Request("Flush"))).Status);
        Assert.Equal("stopped",(await capped.HandleAsync(c.Request("StopSession"))).Status);
    }
    [Fact]
    public async Task 操作結果が不明なら画像保存後も未確定として列挙する()
    {
        using var c = new Context(); await c.Start(); var begin = await c.Session.HandleAsync(c.Request("BeginStep", Guid.NewGuid()));
        var end = await c.Session.HandleAsync(c.Request("EndStep", token: begin.StepToken) with { Outcome = "unknown" });
        Assert.Equal("after-saved", end.Status); Assert.Equal("unknown", end.Outcome); Assert.Contains(begin.StepId, end.PendingSteps);
        var flush = await c.Session.HandleAsync(c.Request("Flush")); Assert.Contains(begin.StepId, flush.PendingSteps);
    }
    [Fact]
    public void 不透明画像は文字や元画素を保持せず固定ローカル出力だけ許可する()
    {
        byte[] png = EvidenceFiles.OpaquePng(new Size(24,24));
        using var input = new MemoryStream(png); using var bitmap = new Bitmap(input);
        for (int y=0;y<bitmap.Height;y++) for(int x=0;x<bitmap.Width;x++) Assert.Equal(Color.Black.ToArgb(), bitmap.GetPixel(x,y).ToArgb());
        Assert.Throws<EvidenceUnavailableException>(() => EvidenceFiles.RequireLocalUnlinkedPath("\\\\server\\output"));
        Assert.Throws<EvidenceUnavailableException>(() => EvidenceFiles.RequireLocalUnlinkedPath("relative"));
        Assert.Throws<EvidenceUnavailableException>(() => EvidenceFiles.OpaquePng(new Size(5000,5000)));
    }
}
