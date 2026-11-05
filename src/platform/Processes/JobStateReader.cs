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
