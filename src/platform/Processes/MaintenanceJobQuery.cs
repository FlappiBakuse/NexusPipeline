using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Networking;

namespace NexusPipeline.Platform.Processes;

[SupportedOSPlatform("windows")]
internal sealed class MaintenanceJobQuery : IDisposable
{
    private readonly object _gate = new();
    private SafeKernelHandle? _job;

    private MaintenanceJobQuery(SafeKernelHandle job) => _job = job;

    internal static MaintenanceJobQuery Retain(SafeKernelHandle actualJob)
    {
        if (!JobNative.DuplicateHandle(new(-1), actualJob, new(-1), out var local, 4, false, 0)) throw Failure();
        try { _ = JobStateReader.Limits(local); return new(local); }
        catch { local.Dispose(); throw; }
    }

    internal static MaintenanceJobQuery Receive(NativeClientBinding actualPeer, ProcessIdentity expectedPeer, long remoteValue)
    {
        if (remoteValue <= 0 || IntPtr.Size == 4 && remoteValue > int.MaxValue || !actualPeer.IsRegistered(expectedPeer)) throw Invalid();
        return actualPeer.UseVerifiedLifetime(() =>
        {
            using var producer = JobNative.OpenProcess(0x101040, false, expectedPeer.ProcessId);
            if (producer.IsInvalid || JobNative.WaitForSingleObject(producer, 0) != 258 ||
                ProcessIdentityReader.Read(producer, expectedPeer.ProcessId) != expectedPeer) throw Invalid();
            if (!DuplicateRemoteQuery(producer, new(remoteValue), new(-1), out var local, 4, false, 0)) throw Failure();
            try
            {
                _ = JobStateReader.Limits(local);
                if (!actualPeer.IsLive || JobNative.WaitForSingleObject(producer, 0) != 258 ||
                    ProcessIdentityReader.Read(producer, expectedPeer.ProcessId) != expectedPeer) throw Invalid();
                return new MaintenanceJobQuery(local);
            }
            catch { local.Dispose(); throw; }
        });
    }

    internal long ExportValue
    {
        get { lock (_gate) return RequireHeld().DangerousGetHandle().ToInt64(); }
    }

    internal void RequireSameObject(MaintenanceJobQuery other)
    {
        using var counterpart = other.Clone();
        lock (_gate)
            if (!CompareObjectHandles(RequireHeld(), counterpart.RequireHeld())) throw Invalid();
    }

    internal void RequireMember(ProcessIdentity expected)
    {
        lock (_gate)
        {
            using var process = JobNative.OpenProcess(0x101000, false, expected.ProcessId);
            if (process.IsInvalid || JobNative.WaitForSingleObject(process, 0) != 258 ||
                ProcessIdentityReader.Read(process, expected.ProcessId) != expected ||
                !JobNative.IsProcessInJob(process, RequireHeld(), out var member) || !member) throw Invalid();
        }
    }

    internal WorkerJobAccounting Accounting
    {
        get { lock (_gate) return JobStateReader.Accounting(RequireHeld()); }
    }

    internal void RequireExcluded(SafeKernelHandle actualProcess)
    {
        lock (_gate)
            if (JobNative.WaitForSingleObject(actualProcess, 0) != 258 ||
                !JobNative.IsProcessInJob(actualProcess, RequireHeld(), out var member) || member) throw Invalid();
    }

    internal uint Flags
    {
        get { lock (_gate) return JobStateReader.Limits(RequireHeld()).Basic.Flags; }
    }

    internal IReadOnlyList<int> ActiveProcessIds
    {
        get { lock (_gate) return JobStateReader.ActiveProcessIds(RequireHeld()); }
    }

    public void Dispose()
    {
        lock (_gate) Interlocked.Exchange(ref _job, null)?.Dispose();
    }

    private SafeKernelHandle RequireHeld() => _job is { IsClosed: false, IsInvalid: false } job ? job : throw Invalid();
    internal MaintenanceJobQuery Clone() { lock (_gate) return Retain(RequireHeld()); }
    private static IOException Invalid() => new("maintenance_job_query_unbound");
    private static Win32Exception Failure() => new(Marshal.GetLastPInvokeError());

    [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateRemoteQuery(SafeKernelHandle producer, IntPtr remoteSource, IntPtr target,
        out SafeKernelHandle local, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [DllImport("kernelbase.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CompareObjectHandles(SafeKernelHandle first, SafeKernelHandle second);
}
