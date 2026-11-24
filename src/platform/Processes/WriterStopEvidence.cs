using NexusPipeline.Core.Execution;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

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

[SupportedOSPlatform("windows")]
internal static class ProcessCreationProbe
{
    internal static bool WasReused(int processId, long originalCreation)
    {
        if (IntPtr.Size != 8 || processId <= 0 || originalCreation <= 0) return false;
        using var self = JobNative.OpenProcess(0x1000, false, Environment.ProcessId);
        if (self.IsInvalid || !JobNative.GetProcessTimes(self, out var selfCreation, out _, out _, out _)) return false;
        if (!NativeLibrary.TryLoad("ntdll.dll", typeof(ProcessCreationProbe).Assembly, DllImportSearchPath.System32, out var library)) return false;
        try
        {
            if (!NativeLibrary.TryGetExport(library, "NtQuerySystemInformation", out var address)) return false;
            var query = Marshal.GetDelegateForFunctionPointer<Query>(address);
            for (var size = 256 * 1024; size <= 16 * 1024 * 1024;)
            {
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    var status = query(5, buffer, size, out var returned);
                    if (status == unchecked((int)0xc0000004))
                    {
                        if (returned > 16 * 1024 * 1024) return false;
                        size = Math.Max(size * 2, returned); continue;
                    }
                    return status == 0 && returned is >= 88 && returned <= size &&
                        Read(buffer, returned, processId, originalCreation, selfCreation);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return false;
        }
        finally { NativeLibrary.Free(library); }
    }

    private static bool Read(IntPtr buffer, int length, int processId, long originalCreation, long selfCreation)
    {
        long? observed = null;
        var selfMatches = false;
        for (int offset = 0, count = 0; offset <= length - 88 && count < 65536; count++)
        {
            var row = Marshal.PtrToStructure<ProcessHead>(buffer + offset);
            if (row.Id == Environment.ProcessId)
            {
                if (selfMatches || row.Created != selfCreation) return false;
                selfMatches = true;
            }
            if (row.Id == processId)
            {
                if (observed is not null || row.Created <= 0) return false;
                observed = row.Created;
            }
            if (row.Next == 0) return selfMatches && observed is > 0 && observed != originalCreation;
            if (row.Next < 88 || row.Next > length - offset - 88) return false;
            offset += checked((int)row.Next);
        }
        return false;
    }

    // Validate this Windows x64 prefix against our own handle before using another process's creation time.
    [StructLayout(LayoutKind.Explicit, Size = 88)]
    private struct ProcessHead
    {
        [FieldOffset(0)] internal uint Next;
        [FieldOffset(32)] internal long Created;
        [FieldOffset(80)] internal nint Id;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Query(int information, IntPtr buffer, int length, out int returned);
}
