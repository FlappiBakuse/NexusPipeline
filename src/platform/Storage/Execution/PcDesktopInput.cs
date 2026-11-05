using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class PcDesktopInput
{
    private Point? _saved;
    private Point _last;

    internal void Position(PcWindowFacts facts, long x, long y, bool foreground)
    {
        if (foreground) Foreground(facts);
        var point = new Point { X = checked((int)x), Y = checked((int)y) };
        if (!ClientToScreen(new(facts.WindowHandle), ref point)) throw new IOException("execution_device_cursor_unavailable");
        if (_saved is null)
        {
            if (!GetCursorPos(out var original)) throw new IOException("execution_device_cursor_unavailable");
            _saved = original;
        }
        _last = point;
        if (foreground) Move(point);
        else if (!SetCursorPos(point.X, point.Y)) throw new IOException("execution_device_cursor_unavailable");
    }

    private static void Move(Point point)
    {
        var left = GetSystemMetrics(76); var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
        if (width <= 0 || height <= 0 || point.X < left || point.Y < top || point.X >= (long)left + width || point.Y >= (long)top + height)
            throw new IOException("execution_device_cursor_unavailable");
        var x = checked((int)(((long)point.X - left) * 65536 / width + 32768 / width));
        var y = checked((int)(((long)point.Y - top) * 65536 / height + 32768 / height));
        Submit(new() { Data = new() { Mouse = new() { X = x, Y = y, Flags = 0xe001 } } });
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromMilliseconds(100))
        {
            if (GetCursorPos(out var actual) && actual.X == point.X && actual.Y == point.Y) return;
            Thread.Sleep(1);
        }
        throw new IOException("execution_device_cursor_interrupted");
    }

    internal void Restore()
    {
        if (_saved is not { } saved) return;
        _saved = null;
        // A user movement after the gesture takes precedence over restoring our saved position.
        if (GetCursorPos(out var current) && current.X == _last.X && current.Y == _last.Y && !SetCursorPos(saved.X, saved.Y))
            throw new IOException("execution_device_cursor_restore_failed");
    }

    internal static void Foreground(PcWindowFacts facts)
    {
        var window = new nint(facts.WindowHandle);
        if (GetForegroundWindow() == window) return;
        if (!SetWindowPos(window, 0, 0, 0, 0, 0, 0x43)) throw new IOException("execution_device_present_failed");
        _ = SetForegroundWindow(window);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (GetForegroundWindow() != window && elapsed.Elapsed < TimeSpan.FromMilliseconds(100)) Thread.Sleep(5);
        if (GetForegroundWindow() != window) throw new IOException("execution_device_foreground_unavailable");
    }

    internal static void Send(PcWindowFacts facts, uint message, long value)
    {
        Foreground(facts);
        switch (message)
        {
            case 0x100: Keyboard(checked((ushort)value), 0, 0); break;
            case 0x101: ReleaseKey(checked((int)value)); break;
            case 0x102:
                try { Keyboard(0, checked((ushort)value), 4); }
                finally { Keyboard(0, checked((ushort)value), 6); }
                break;
            case 0x201: Mouse(2); break;
            case 0x202: ReleasePointer(); break;
            case 0x200: break;
            case 0x20a: Mouse(0x800, unchecked((uint)(int)(short)(value >> 16))); break;
            case 0x20e: Mouse(0x1000, unchecked((uint)(int)(short)(value >> 16))); break;
            default: throw new IOException("execution_device_native_message_invalid");
        }
    }

    internal static void ReleaseKey(int code) => Keyboard(checked((ushort)code), 0, 2);
    internal static void ReleasePointer() => Mouse(4);
    private static void Keyboard(ushort key, ushort scan, uint flags) => Submit(new() {
        Type = 1, Data = new() { Keyboard = new() { Key = key, Scan = scan, Flags = flags } } });
    private static void Mouse(uint flags, uint data = 0) => Submit(new() {
        Type = 0, Data = new() { Mouse = new() { Flags = flags, Data = data } } });
    private static void Submit(Input input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1) throw new IOException("execution_device_native_input_failed");
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { internal uint Type; internal InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    { [FieldOffset(0)] internal MouseData Mouse; [FieldOffset(0)] internal KeyboardData Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData
    { internal int X, Y; internal uint Data, Flags, Time; internal nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData
    { internal ushort Key, Scan; internal uint Flags, Time; internal nuint Extra; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(
        nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint window, ref Point point);
}
