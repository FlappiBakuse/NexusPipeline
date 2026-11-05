using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Processes;

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
