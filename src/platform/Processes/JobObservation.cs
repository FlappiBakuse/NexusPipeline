using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Processes;

[SupportedOSPlatform("windows")]
internal static class JobStateReader
{
    internal static IReadOnlyList<int> ActiveProcessIds(SafeKernelHandle job)
    {
        for (var capacity = 64; capacity <= 4096; capacity *= 2)
        {
            var length = 8 + capacity * IntPtr.Size;
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (JobNative.QueryInformationJobObject(job, 3, buffer, (uint)length, IntPtr.Zero))
                {
                    var count = Marshal.ReadInt32(buffer, 4);
                    if (count < 0 || count > capacity) throw new IOException("Invalid worker job process count");
                    return Enumerable.Range(0, count).Select(index => checked((int)Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size))).ToArray();
                }
                if (Marshal.GetLastPInvokeError() != 234) throw Failure();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("Worker job process inventory exceeded its bound");
    }

    internal static WorkerJobAccounting Accounting(SafeKernelHandle job)
    {
        var value = Query<JobNative.Accounting>(job, 1);
        return new((value.TotalUserTime + value.TotalKernelTime) / 10_000_000.0, value.TotalProcesses, value.ActiveProcesses);
    }

    internal static JobNative.ExtendedLimits Limits(SafeKernelHandle job) => Query<JobNative.ExtendedLimits>(job, 9);

    private static T Query<T>(SafeKernelHandle job, int kind) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!JobNative.QueryInformationJobObject(job, kind, buffer, (uint)size, IntPtr.Zero)) throw Failure();
            return Marshal.PtrToStructure<T>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static Win32Exception Failure() => new(Marshal.GetLastPInvokeError());
}

internal sealed class JobProcessDrain : IDisposable
{
    private readonly List<SafeKernelHandle> _members = [];
    private readonly IReadOnlyList<SafeKernelHandle> _retained;
    public bool IsComplete { get; private set; } = true;

    public JobProcessDrain(SafeKernelHandle job, IReadOnlyList<int> activeIds, IReadOnlyList<SafeKernelHandle> retained)
    {
        _retained = retained;
        var retainedIds = retained.Select(JobNative.GetProcessId).ToHashSet();
        if (retainedIds.Contains(0)) IsComplete = false;
        foreach (var pid in activeIds)
        {
            if (retainedIds.Contains(checked((uint)pid))) continue;
            var process = JobNative.OpenProcess(0x101000, false, pid);
            if (process.IsInvalid || !JobNative.IsProcessInJob(process, job, out var owned) || !owned)
            {
                IsComplete = false;
                process.Dispose();
                continue;
            }
            _members.Add(process);
        }
    }

    public bool AllSignaled => IsComplete && _retained.Concat(_members).All(process => JobNative.WaitForSingleObject(process, 0) == 0);

    public void Dispose()
    {
        foreach (var process in _members) process.Dispose();
    }
}

[SupportedOSPlatform("windows")]
internal static class StoppedJobFamily
{
    internal static WorkerJobAccounting Require(SafeKernelHandle job, IReadOnlyList<SafeKernelHandle> retained)
    {
        var before = JobStateReader.Accounting(job);
        if (before.TotalProcesses == 0 || before.ActiveProcesses != 0 || JobStateReader.ActiveProcessIds(job).Count != 0 ||
            JobStateReader.Limits(job).Basic.Flags != 0x2000) throw Invalid();
        var identities = new HashSet<(uint Pid, long Created)>();
        foreach (var process in retained)
        {
            var pid = JobNative.GetProcessId(process);
            if (pid == 0 || !JobNative.GetProcessTimes(process, out var created, out _, out _, out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            identities.Add((pid, created));
        }
        // Ended children need their original kernel objects, not the live metadata needed to retain a running game.
        using var drain = new JobProcessDrain(job, [], retained);
        var after = JobStateReader.Accounting(job);
        if (before.TotalProcesses != after.TotalProcesses || after.ActiveProcesses != 0 || identities.Count != after.TotalProcesses ||
            !drain.IsComplete || !drain.AllSignaled || JobStateReader.ActiveProcessIds(job).Count != 0 ||
            JobStateReader.Limits(job).Basic.Flags != 0x2000) throw Invalid();
        return after;
    }

    private static IOException Invalid() => new("worker_stopped_family_unverified");
}
