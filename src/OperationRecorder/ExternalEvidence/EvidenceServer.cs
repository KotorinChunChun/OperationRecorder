using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace OperationRecorderApp.ExternalEvidence;

public static class EvidenceServer
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    public static async Task<int> RunAsync(string name, int ownerPid, long ownerStartTicks, IEvidenceBackend backend, IEvidenceTargetValidator validator)
    {
        if (!name.StartsWith("oprec-evidence-", StringComparison.Ordinal) || !Guid.TryParseExact(name.Substring(15), "N", out _) || ownerPid <= 0) return 2;
        bool OwnerAlive()
        { try { using var owner = Process.GetProcessById(ownerPid); using var self = Process.GetCurrentProcess(); return !owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == ownerStartTicks && owner.SessionId == self.SessionId; } catch { return false; } }
        if (!OwnerAlive()) return 3;
        using var stop = new CancellationTokenSource();
        int finished = 0;
        var monitor = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (!OwnerAlive())
                {
                    stop.Cancel(); await Task.Delay(500);
                    if (Volatile.Read(ref finished) == 0) Environment.Exit(3); // Broker only. Kernel job closes workers/encoders.
                    return;
                }
                try { await Task.Delay(200, stop.Token); } catch { return; }
            }
        });
        try
        {
            using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); connect.CancelAfter(10000);
            await pipe.WaitForConnectionAsync(connect.Token);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientPid) || clientPid != ownerPid || !OwnerAlive()) return 3;
            using var session = new EvidenceSession(backend, validator);
            while (!stop.IsCancellationRequested)
            {
                EvidenceRequest request;
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); idle.CancelAfter(60000);
                try { request = await EvidenceWire.ReadAsync<EvidenceRequest>(pipe, idle.Token); }
                catch (EndOfStreamException) { break; }
                catch { await EvidenceWire.WriteAsync(pipe, new EvidenceReply { Status = "invalid-request" }, stop.Token); return 2; }
                var response = await session.HandleAsync(request, stop.Token);
                using var write = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); write.CancelAfter(1000);
                await EvidenceWire.WriteAsync(pipe, response, write.Token);
                if (request.Method == "StopSession" && response.Status == "stopped") break;
            }
            return 0;
        }
        catch { return 1; }
        finally { Interlocked.Exchange(ref finished, 1); stop.Cancel(); }
    }
}
public static class ExternalEvidenceCommand
{
    public static bool IsEvidence(string[] args) => args.Length > 0 && args[0].StartsWith("--evidence-", StringComparison.Ordinal);
    public static int Execute(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--evidence-worker") return EvidenceWorker.RunAsync().GetAwaiter().GetResult();
            if (args.Length == 1 && args[0] == "--evidence-layout-candidate") return PasswordLayoutCandidate.RunAsync().GetAwaiter().GetResult();
            if (args.Length != 4 || args[0] != "--evidence-host" || !int.TryParse(args[2], out int pid) || !long.TryParse(args[3], out long ticks)) return 2;
            using var validator = new WindowsEvidenceValidator();
            var backend = new EvidenceWorkerBackend(() => { var info = new ProcessStartInfo(Environment.ProcessPath); info.ArgumentList.Add("--evidence-worker"); return info; });
            return EvidenceServer.RunAsync(args[1], pid, ticks, backend, validator).GetAwaiter().GetResult();
        }
        catch { return 1; }
    }
}
