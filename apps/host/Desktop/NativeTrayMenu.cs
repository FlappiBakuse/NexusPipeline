using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NexusPipeline.Host.Desktop;

[SupportedOSPlatform("windows")]
internal static class NativeTrayMenu
{
    internal static Task<int> ShowAsync(bool browser, bool busy, bool ready, bool failed, bool chinese)
    {
        var result = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(Show(browser, busy, ready, failed, chinese)); }
            catch (Exception error) { result.SetException(error); }
        }) { IsBackground = true, Name = "Nexus tray menu" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    private static int Show(bool browser, bool busy, bool ready, bool failed, bool chinese)
    {
        var owner = CreateWindowExW(0x80, "STATIC", "NexusPipeline", 0x90000000, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (owner == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var menu = CreatePopupMenu();
        try
        {
            if (menu == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var disabled = busy || !ready ? 3u : 0u;
            Add(disabled | (browser ? 8u : 0u), 1, chinese ? "轻量模式" : "Lightweight mode");
            Add(disabled, 2, chinese ? "页面重载" : "Reload page");
            Add(0x800, 0, null);
            Add(busy || !ready && !failed ? 3u : 0u, 3, chinese ? "退出 NexusPipeline" : "Quit NexusPipeline");
            if (!GetCursorPos(out var point)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (!Focus(owner)) return 0;
            var selected = TrackPopupMenuEx(menu, 0x100 | 0x2, point.X, point.Y, owner, IntPtr.Zero);
            PostMessageW(owner, 0, UIntPtr.Zero, IntPtr.Zero);
            return selected;
        }
        finally { if (menu != IntPtr.Zero) DestroyMenu(menu); DestroyWindow(owner); }

        void Add(uint flags, uint id, string? title)
        {
            if (!AppendMenuW(menu, flags, new UIntPtr(id), title)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static bool Focus(IntPtr owner)
    {
        var current = GetCurrentThreadId();
        var foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        // The input arrives in Electron; the native menu lives on this Host thread.
        var attached = foreground != 0 && foreground != current && AttachThreadInput(current, foreground, true);
        try
        {
            return SetForegroundWindow(owner) && GetForegroundWindow() == owner;
        }
        finally { if (attached) AttachThreadInput(current, foreground, false); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { internal int X, Y; }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint source, uint target, [MarshalAs(UnmanagedType.Bool)] bool attach);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint extended, string kind, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? title);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr options);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessageW(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
}
