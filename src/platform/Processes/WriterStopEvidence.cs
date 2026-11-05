using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;

namespace NexusPipeline.Platform.Processes;

[SupportedOSPlatform("windows")]
public static class WriterStopEvidence
{
    public static WriterState ObserveProcess(ProcessIdentity identity)
    {
        using var process = JobNative.OpenProcess(0x00101000, false, identity.ProcessId);
        if (process.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            return error == 87 || error == 5 && ProcessCreationProbe.WasReused(identity.ProcessId, identity.CreatedAtUtc.ToFileTimeUtc())
                ? WriterState.Stopped : WriterState.Unknown;
        }
        if (!JobNative.GetProcessTimes(process, out var created, out _, out _, out _)) return WriterState.Unknown;
        if (created != identity.CreatedAtUtc.ToFileTimeUtc()) return WriterState.Stopped;
        return JobNative.WaitForSingleObject(process, 0) switch
        {
            0 => WriterState.Stopped,
            258 => WriterState.Running,
            _ => WriterState.Unknown
        };
    }

    public static WriterState ObserveJob(string name, int sessionId)
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        if (sessionId != current.SessionId) return WriterState.Unknown;
        if (!name.StartsWith("Local\\NexusWorker-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[18..], "N", out _)) return WriterState.Unknown;
        using var job = JobNative.OpenJobObjectW(4, false, name);
        if (job.IsInvalid) return WriterState.Unknown;
        var size = Marshal.SizeOf<JobNative.Accounting>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!JobNative.QueryInformationJobObject(job, 1, buffer, (uint)size, IntPtr.Zero)) return WriterState.Unknown;
            return Marshal.PtrToStructure<JobNative.Accounting>(buffer).ActiveProcesses == 0 ? WriterState.Stopped : WriterState.Running;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
