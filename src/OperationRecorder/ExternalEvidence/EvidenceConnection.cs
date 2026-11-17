using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OperationRecorderApp.ExternalEvidence;

public static class EvidenceProcessEnvironment
{
    public static void Scrub(ProcessStartInfo info)
    {
        info.Environment.Clear();
        foreach (string key in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "PATH", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_MULTILEVEL_LOOKUP", "DOTNET_ROLL_FORWARD" })
        { var value = Environment.GetEnvironmentVariable(key); if (value != null) info.Environment[key] = value; }
    }
}
public sealed class EvidenceConnection : IDisposable
{
    readonly NamedPipeClientStream pipe;
    readonly Process broker;
    readonly SemaphoreSlim gate = new(1,1);
    readonly int brokerPid;
    int disposed;
    public int BrokerPid => brokerPid;
    public EvidenceConnection(string name)
    { pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous); }
    EvidenceConnection(string name, Process broker) : this(name) { this.broker = broker; brokerPid = broker.Id; }
    public static async Task<EvidenceConnection> StartAsync(string executable, int connectTimeoutMs = 10000)
    {
        if (connectTimeoutMs < 100 || connectTimeoutMs > 30000) throw new ArgumentOutOfRangeException(nameof(connectTimeoutMs));
        using var owner = Process.GetCurrentProcess(); string name = "oprec-evidence-" + Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true };
        EvidenceProcessEnvironment.Scrub(info);
        foreach (string arg in new[] { "--evidence-host", name, owner.Id.ToString(), owner.StartTime.ToUniversalTime().Ticks.ToString() }) info.ArgumentList.Add(arg);
        var process = Process.Start(info) ?? throw new EvidenceUnavailableException("broker-unavailable");
        var connection = new EvidenceConnection(name, process);
        try { await connection.ConnectAsync(connectTimeoutMs); return connection; } catch { connection.Dispose(); throw; }
    }
    public async Task ConnectAsync(int timeoutMs = 10000)
    { using var timeout = new CancellationTokenSource(timeoutMs); await pipe.ConnectAsync(timeout.Token); }
    public async Task<EvidenceReply> SendAsync(EvidenceRequest request, int timeoutMs = 10000)
    {
        if (timeoutMs < 50 || timeoutMs > 30000) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        using var deadline = new CancellationTokenSource(timeoutMs);
        bool entered = false;
        try
        {
            await gate.WaitAsync(deadline.Token); entered = true;
            await EvidenceWire.WriteAsync(pipe, request, deadline.Token);
            var response = await EvidenceWire.ReadAsync<EvidenceReply>(pipe, deadline.Token);
            if (response.RequestId != request.RequestId || response.SessionId != Guid.Empty && response.SessionId != request.SessionId) throw new EvidenceUnavailableException("response-mismatch");
            return response;
        }
        catch
        {
            // A timed-out connection is poisoned; never read a late response as a later request's reply.
            Dispose();
            return new EvidenceReply { RequestId = request.RequestId, SessionId = request.SessionId, StepId = request.StepId, StepToken = request.StepToken,
                Status = "response-unconfirmed", OperationAllowed = false };
        }
        finally { if (entered) gate.Release(); }
    }
    public async Task<string> SendJsonAsync(string request, int timeoutMs = 10000)
    {
        EvidenceRequest parsed;
        try { parsed = JsonSerializer.Deserialize<EvidenceRequest>(request, EvidenceProtocol.Json); }
        catch { return JsonSerializer.Serialize(new EvidenceReply { Status = "invalid-request" }, EvidenceProtocol.Json); }
        if (parsed == null) return JsonSerializer.Serialize(new EvidenceReply { Status = "invalid-request" }, EvidenceProtocol.Json);
        return JsonSerializer.Serialize(await SendAsync(parsed, timeoutMs), EvidenceProtocol.Json);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        pipe.Dispose();
        if (broker != null) { try { if (!broker.HasExited) broker.Kill(true); } catch { } }
        broker?.Dispose(); // Only our dedicated broker; the target app is never terminated here.
    }
}
