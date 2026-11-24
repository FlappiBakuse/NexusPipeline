using NexusPipeline.Core.Execution;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

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
        using var timing = Storage.StartupProfile.Measure("readiness.process-snapshot");
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

internal sealed record ObservedWindow(string Hwnd, int Pid, string Name, string Title, bool Foreground);
internal sealed record ObservedWindows(IReadOnlyList<ObservedWindow> Windows, bool Truncated);

[SupportedOSPlatform("windows")]
internal static class WindowSnapshotProbe
{
    internal const int MaximumResults = 512;

    private sealed class WindowCount
    {
        internal int Count;
        internal bool Truncated;
    }

    internal static ObservedWindows Capture(int? pid, string titleContains, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var timing = Storage.StartupProfile.Measure("readiness.window-snapshot");
        var processes = ProcessSnapshotProbe.Capture(null, "", cancellation);
        var names = processes.Processes.ToDictionary(value => value.Pid, value => value.Name);
        var result = new List<ObservedWindow>();
        var foreground = GetForegroundWindow();
        var truncated = processes.Truncated;
        var titleUnavailable = false;
        var enumerationTruncated = EnumerateVisible(cancellation, (window, processId) =>
        {
            if (pid is not null && processId != pid) return true;
            if (!TryReadTitle(window, processId, out var text, out var titleTruncated))
            { titleUnavailable = true; return false; }
            truncated |= titleTruncated;
            if (!text.Contains(titleContains, StringComparison.OrdinalIgnoreCase)) return true;
            if (result.Count == MaximumResults) { truncated = true; return false; }
            result.Add(new(window.ToInt64().ToString(CultureInfo.InvariantCulture), processId,
                names.GetValueOrDefault(processId, ""), text, window == foreground));
            return true;
        });
        if (titleUnavailable) throw new IOException("judgement_window_title_unavailable");
        return new(result.OrderBy(value => value.Pid).ThenBy(value => long.Parse(value.Hwnd, CultureInfo.InvariantCulture)).ToArray(),
            truncated || enumerationTruncated);
    }

    internal static IReadOnlyDictionary<int, ExecutionTargetReadiness> CaptureReadiness(IEnumerable<int> processIds, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var timing = Storage.StartupProfile.Measure("readiness.window-snapshot");
        var windows = processIds.ToDictionary(pid => pid, _ => new WindowCount());
        var truncated = EnumerateVisible(cancellation, (window, processId) =>
        {
            if (!windows.TryGetValue(processId, out var count) || count.Truncated) return true;
            if (count.Count == MaximumResults || !TryReadTitle(window, processId, out _, out var titleTruncated) || titleTruncated)
                count.Truncated = true;
            else count.Count++;
            return true;
        });
        return windows.ToDictionary(pair => pair.Key, pair => truncated || pair.Value.Truncated
            ? ExecutionTargetReadiness.Unknown : pair.Value.Count == 0 ? ExecutionTargetReadiness.NotReady : ExecutionTargetReadiness.Ready);
    }

    private static bool EnumerateVisible(CancellationToken cancellation, Func<IntPtr, int, bool> visit)
    {
        var visited = 0;
        var truncated = false;
        var stopped = false;
        bool Visit(IntPtr window, IntPtr unused)
        {
            if (cancellation.IsCancellationRequested) { stopped = true; return false; }
            if (++visited > 65536) { truncated = true; stopped = true; return false; }
            if (!IsWindowVisible(window)) return true;
            GetWindowThreadProcessId(window, out var processId);
            if (processId is 0 or > int.MaxValue) return true;
            if (visit(window, (int)processId)) return true;
            stopped = true;
            return false;
        }
        if (!EnumWindows(Visit, IntPtr.Zero) && !stopped) throw new Win32Exception(Marshal.GetLastPInvokeError());
        cancellation.ThrowIfCancellationRequested();
        return truncated;
    }

    private static bool TryReadTitle(IntPtr window, int processId, out string text, out bool truncated)
    {
        var title = new StringBuilder(257);
        if (processId == Environment.ProcessId)
        {
            // GetWindowText sends to our own UI thread synchronously, so a hung window needs a finite send.
            if (SendMessageTimeoutW(window, 0xe, UIntPtr.Zero, IntPtr.Zero, 0x23, 100, out var length) == IntPtr.Zero ||
                SendMessageTimeoutW(window, 0xd, (UIntPtr)title.Capacity, title, 0x23, 100, out _) == IntPtr.Zero)
            { text = ""; truncated = false; return false; }
            truncated = length.ToUInt64() > 256;
        }
        else
        {
            truncated = GetWindowTextLengthW(window) > 256;
            GetWindowTextW(window, title, title.Capacity);
        }
        text = title.ToString();
        return true;
    }

    private delegate bool WindowVisitor(IntPtr window, IntPtr state);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowVisitor callback, IntPtr state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int maximum);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMilliseconds, out UIntPtr result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, UIntPtr wParam, StringBuilder text,
        uint flags, uint timeoutMilliseconds, out UIntPtr result);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
