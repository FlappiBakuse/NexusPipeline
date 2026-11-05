using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Processes;

internal sealed record ObservedProcess(int Pid, int Ppid, string Name, DateTime? StartTimeUtc);
internal sealed record ObservedProcesses(IReadOnlyList<ObservedProcess> Processes, bool Truncated);

[SupportedOSPlatform("windows")]
internal static class ProcessSnapshotProbe
{
    internal const int MaximumResults = 2048;

    internal static ObservedProcesses Capture(int? pid, string nameContains, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>() };
        if (!Process32FirstW(snapshot, ref entry))
        {
            if (Marshal.GetLastPInvokeError() == 18) return new([], false);
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        var result = new List<ObservedProcess>();
        var visited = 0;
        do
        {
            cancellation.ThrowIfCancellationRequested();
            if (++visited > 65536) return Finish(result, true);
            if (entry.ProcessId is > 0 and <= int.MaxValue && entry.ParentProcessId <= int.MaxValue &&
                (pid is null || entry.ProcessId == pid) && entry.ImageName.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            {
                if (result.Count == MaximumResults) return Finish(result, true);
                result.Add(new((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ImageName, CreatedAt((int)entry.ProcessId)));
            }
            entry.Size = (uint)Marshal.SizeOf<Entry>();
        } while (Process32NextW(snapshot, ref entry));
        if (Marshal.GetLastPInvokeError() != 18) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return Finish(result, false);
    }

    private static DateTime? CreatedAt(int pid)
    {
        using var process = JobNative.OpenProcess(0x1000, false, pid);
        if (process.IsInvalid || !JobNative.GetProcessTimes(process, out var created, out _, out _, out _)) return null;
        return DateTime.FromFileTimeUtc(created);
    }

    private static ObservedProcesses Finish(List<ObservedProcess> values, bool truncated) =>
        new(values.OrderBy(value => value.Pid).ToArray(), truncated);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Entry
    {
        internal uint Size, Usage, ProcessId;
        internal UIntPtr DefaultHeap;
        internal uint ModuleId, Threads, ParentProcessId;
        internal int BasePriority;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string ImageName;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeKernelHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeKernelHandle snapshot, ref Entry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeKernelHandle snapshot, ref Entry entry);
}
