using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace NexusPipeline.Platform.Processes;

internal sealed record ObservedWindow(string Hwnd, int Pid, string Name, string Title, bool Foreground);
internal sealed record ObservedWindows(IReadOnlyList<ObservedWindow> Windows, bool Truncated);

[SupportedOSPlatform("windows")]
internal static class WindowSnapshotProbe
{
    internal const int MaximumResults = 512;

    internal static ObservedWindows Capture(int? pid, string titleContains, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var processes = ProcessSnapshotProbe.Capture(null, "", cancellation);
        var names = processes.Processes.ToDictionary(value => value.Pid, value => value.Name);
        var result = new List<ObservedWindow>();
        var foreground = GetForegroundWindow();
        var visited = 0;
        var truncated = processes.Truncated;
        var cancelled = false;
        var stopped = false;
        var titleUnavailable = false;
        bool Visit(IntPtr window, IntPtr unused)
        {
            if (cancellation.IsCancellationRequested) { cancelled = true; stopped = true; return false; }
            if (++visited > 65536) { truncated = true; stopped = true; return false; }
            if (!IsWindowVisible(window)) return true;
            GetWindowThreadProcessId(window, out var processId);
            if (processId is 0 or > int.MaxValue || pid is not null && processId != pid) return true;
            var title = new StringBuilder(257);
            if (processId == Environment.ProcessId)
            {
                // GetWindowText sends to our own UI thread synchronously, so a hung window needs a finite send.
                if (SendMessageTimeoutW(window, 0xe, UIntPtr.Zero, IntPtr.Zero, 0x23, 100, out var length) == IntPtr.Zero ||
                    SendMessageTimeoutW(window, 0xd, (UIntPtr)title.Capacity, title, 0x23, 100, out _) == IntPtr.Zero)
                { titleUnavailable = true; stopped = true; return false; }
                if (length.ToUInt64() > 256) truncated = true;
            }
            else
            {
                if (GetWindowTextLengthW(window) > 256) truncated = true;
                GetWindowTextW(window, title, title.Capacity);
            }
            var text = title.ToString();
            if (!text.Contains(titleContains, StringComparison.OrdinalIgnoreCase)) return true;
            if (result.Count == MaximumResults) { truncated = true; stopped = true; return false; }
            result.Add(new(window.ToInt64().ToString(CultureInfo.InvariantCulture), (int)processId,
                names.GetValueOrDefault((int)processId, ""), text, window == foreground));
            return true;
        }
        if (!EnumWindows(Visit, IntPtr.Zero) && !stopped) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (cancelled) cancellation.ThrowIfCancellationRequested();
        if (titleUnavailable) throw new IOException("judgement_window_title_unavailable");
        cancellation.ThrowIfCancellationRequested();
        return new(result.OrderBy(value => value.Pid).ThenBy(value => long.Parse(value.Hwnd, CultureInfo.InvariantCulture)).ToArray(), truncated);
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
