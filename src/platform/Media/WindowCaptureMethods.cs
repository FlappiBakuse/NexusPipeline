using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Media;

[SupportedOSPlatform("windows")]
internal static class WindowCaptureMethods
{
    internal static void Capture(IntPtr window, IntPtr bitmap, int width, int height, string method)
    {
        if (method == "printWindow")
        {
            if (!PrintWindow(window, bitmap, 1) || !GdiFlush()) throw Unavailable();
            return;
        }
        if (method is not ("gdi" or "screenDc")) throw new IOException("capture_method_invalid");
        var origin = new Point();
        var sourceWindow = method == "gdi" ? window : IntPtr.Zero;
        if (method == "screenDc")
        {
            if (!ClientToScreen(window, ref origin) || DwmFlush() < 0)
                throw new IOException("capture_composition_unavailable");
            var left = GetSystemMetrics(76); var top = GetSystemMetrics(77);
            if (origin.X < left || origin.Y < top || origin.X + width > left + GetSystemMetrics(78) ||
                origin.Y + height > top + GetSystemMetrics(79)) throw new IOException("capture_window_offscreen");
            RequireVisible(window, origin, width, height);
        }
        var source = GetDC(sourceWindow);
        if (source == IntPtr.Zero) throw Unavailable();
        try
        {
            if (!BitBlt(bitmap, 0, 0, width, height, source, origin.X, origin.Y, 0x00cc0020) || !GdiFlush()) throw Unavailable();
            if (method == "screenDc") RequireVisible(window, origin, width, height);
        }
        finally { _ = ReleaseDC(sourceWindow, source); }
    }
    private static IOException Unavailable() => new("capture_window_unavailable");
    private static void RequireVisible(IntPtr window, Point origin, int width, int height)
    {
        var currentOrigin = new Point();
        if (!ClientToScreen(window, ref currentOrigin) || currentOrigin.X != origin.X || currentOrigin.Y != origin.Y)
            throw Unavailable();
        var current = GetTopWindow(IntPtr.Zero);
        for (var count = 0; current != IntPtr.Zero && count < 10000; count++, current = GetWindow(current, 2))
        {
            if (current == window) return;
            if (!IsWindowVisible(current) || IsIconic(current)) continue;
            if (WindowAttribute(current, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            if (!GetWindowRect(current, out var bounds)) throw Unavailable();
            if (bounds.Left < origin.X + width && bounds.Right > origin.X &&
                bounds.Top < origin.Y + height && bounds.Bottom > origin.Y) throw new IOException("capture_window_occluded");
        }
        throw Unavailable();
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int WindowAttribute(IntPtr window, uint attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PrintWindow(IntPtr window, IntPtr context, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr context);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr target,
        int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
}
