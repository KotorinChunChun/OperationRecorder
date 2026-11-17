using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace OperationRecorderApp.ExternalEvidence;

// Kernel closes this handle on broker death, killing workers and encoder descendants even on a crash.
sealed class EvidenceProcessJob : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public UIntPtr Minimum, Maximum; public uint Count; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct Io { public ulong A,B,C,D,E,F; }
    [StructLayout(LayoutKind.Sequential)] struct Limits { public BasicLimits Basic; public Io Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref Limits limits, uint length);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    readonly SafeFileHandle handle;
    public EvidenceProcessJob(Process process)
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        var limits = new Limits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (handle.IsInvalid || !SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<Limits>()) || !AssignProcessToJobObject(handle, process.Handle))
        { handle.Dispose(); throw new EvidenceUnavailableException("worker-unavailable"); }
    }
    public void Dispose() => handle.Dispose();
}
public sealed class EvidenceWorkerBackend : IEvidenceBackend
{
    readonly Func<ProcessStartInfo> factory;
    public EvidenceWorkerBackend(Func<ProcessStartInfo> factory) { this.factory = factory; }
    public async Task<string> CaptureAndSaveAsync(CaptureWork work, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        var remaining = work.DeadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new EvidenceUnavailableException("deadline-exceeded");
        deadline.CancelAfter(remaining); var cancellation = deadline.Token;
        var info = factory(); info.UseShellExecute = false; info.CreateNoWindow = true;
        EvidenceProcessEnvironment.Scrub(info);
        info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new EvidenceUnavailableException("worker-unavailable");
            using var job = new EvidenceProcessJob(process);
            var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellation);
            await EvidenceWire.WriteAsync(process.StandardInput.BaseStream, work, cancellation);
            process.StandardInput.Close();
            var result = await EvidenceWire.ReadAsync<WorkerReply>(process.StandardOutput.BaseStream, cancellation);
            await process.WaitForExitAsync(cancellation);
            if (process.ExitCode != 0 || result.Status != "saved")
                throw new EvidenceUnavailableException(result.Status is "target-mismatch" or "deadline-exceeded" or "save-unavailable" or "video-unavailable" ? result.Status : "capture-unavailable");
            return Path.Combine(work.Directory, "commit.json");
        }
        catch (OperationCanceledException) { throw new EvidenceUnavailableException(DateTimeOffset.UtcNow >= work.DeadlineUtc ? "deadline-exceeded" : "caller-unavailable"); }
        catch (EvidenceUnavailableException) { throw; }
        catch { throw new EvidenceUnavailableException("worker-unavailable"); }
        finally { try { if (!process.HasExited) process.Kill(true); } catch { } }
    }
}
public static class EvidenceVideo
{
    // A segment is one fully masked frame. It is explicitly not a continuous recording of intervening actions.
    public static async Task<byte[]> EncodeAsync(string executable, byte[] protectedPng, CancellationToken token)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        EvidenceProcessEnvironment.Scrub(info);
        foreach (var arg in new[] { "-loglevel", "quiet", "-nostdin", "-f", "image2pipe", "-vcodec", "png", "-i", "pipe:0", "-frames:v", "1", "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-movflags", "frag_keyframe+empty_moov", "-f", "mp4", "pipe:1" }) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new EvidenceUnavailableException("video-unavailable");
            using var job = new EvidenceProcessJob(process);
            var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
            var reading = EvidenceWire.ReadBoundedToEndAsync(process.StandardOutput.BaseStream, 8388608, token);
            await process.StandardInput.BaseStream.WriteAsync(protectedPng, token); process.StandardInput.Close();
            byte[] video = await reading; await process.WaitForExitAsync(token);
            if (process.ExitCode != 0 || video.Length < 32) throw new EvidenceUnavailableException("video-unavailable");
            return video;
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new EvidenceUnavailableException("video-unavailable"); }
        finally { try { if (!process.HasExited) process.Kill(true); } catch { } }
    }
}
