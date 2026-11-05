using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Processes;

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
