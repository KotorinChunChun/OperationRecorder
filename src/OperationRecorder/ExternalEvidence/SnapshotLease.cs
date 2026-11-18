using System;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace OperationRecorderApp.ExternalEvidence;

internal static class SnapshotLease
{
    public static Task<T> RunAsync<T>(Func<Task<T>> capture, CancellationToken token)
    {
        // Keep the same mutex owner thread through asynchronous worker cleanup.
        return Task.Factory.StartNew(() => Run(capture, token), token,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
    }

    static T Run<T>(Func<Task<T>> capture, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new EvidenceUnavailableException("snapshot-unavailable");
        // Local\ limits the mutex to this Windows session. The SID separates its users.
        using var mutex = new Mutex(false, @"Local\OperationRecorder.Snapshot." + sid);
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new EvidenceUnavailableException("snapshot-busy");
        try
        {
            token.ThrowIfCancellationRequested();
            return capture().GetAwaiter().GetResult();
        }
        finally { mutex.ReleaseMutex(); }
    }
}
