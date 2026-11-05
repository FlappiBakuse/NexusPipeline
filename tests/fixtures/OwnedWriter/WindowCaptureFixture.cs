using System.Runtime.InteropServices;

internal static class WindowCaptureFixture
{
    private static readonly WindowProcedure Procedure = Dispatch;
    private static int _printedFrames;
    private static bool _staticFrame;
    private static bool _inverse;
    private static string? _inputReport;

    internal static int Run(string[] args)
    {
        if (args.Length is not (1 or 2) || args.Length == 2 && args[1] is not ("static-frame" or "screen-frame" or "retry-static-frame" or "inverse-frame")) return 2;
        var report = args.Length == 2 && args[1] == "retry-static-frame" ? args[0] + "." + Environment.ProcessId : args[0];
        _inputReport = report + ".input";
        _staticFrame = args.Length == 2;
        _inverse = args.Length == 2 && args[1] == "inverse-frame";
        OwnedFixtureEvidence.RequirePath(report);
        var name = "NexusCaptureFixture-" + Guid.NewGuid().ToString("N");
        var module = GetModuleHandleW(null);
        var registration = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Procedure,
            Instance = module, ClassName = name, Background = new IntPtr(6) };
        if (RegisterClassExW(ref registration) == 0) return 3;
        const uint style = 0x00cf0000;
        var bounds = new Rect { Right = 640, Bottom = 480 };
        if (!AdjustWindowRect(ref bounds, style, false, 0)) return 4;
        var window = CreateWindowExW(args.Length == 2 && args[1] == "screen-frame" ? 8u : 0u, name, "NexusPipeline owned capture fixture", style,
            40, 40, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
            IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
        if (window == IntPtr.Zero) return 4;
        ShowWindow(window, 4); UpdateWindow(window);
        GetClientRect(window, out var client);
        OwnedFixtureEvidence.WriteReport(report, new { status = "windowReady", processId = Environment.ProcessId,
            windowHandle = window.ToInt64(), width = client.Right, height = client.Bottom });
        while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        { TranslateMessage(ref message); DispatchMessageW(ref message); }
        return 0;
    }

    private static IntPtr Dispatch(IntPtr window, uint message, IntPtr wparam, IntPtr lparam)
    {
        if (message is 0x100 or 0x101 or 0x102 or 0x200 or 0x201 or 0x202 or 0x20a or 0x20e)
            File.AppendAllText(_inputReport!, System.Text.Json.JsonSerializer.Serialize(new { message, value = wparam.ToInt64(), parameter = lparam.ToInt64() }) + "\n");
        if (message is 0x317 or 0x318)
        {
            Paint(window, wparam, _staticFrame ? 1 : Interlocked.Increment(ref _printedFrames));
            return IntPtr.Zero;
        }
        if (message == 0xf)
        {
            var paint = new PaintState { Reserved = new byte[32] };
            var device = BeginPaint(window, ref paint);
            try { Paint(window, device, _staticFrame ? 1 : Interlocked.Increment(ref _printedFrames)); }
            finally { EndPaint(window, ref paint); }
            return IntPtr.Zero;
        }
        if (message == 2) { PostQuitMessage(0); return IntPtr.Zero; }
        return DefWindowProcW(window, message, wparam, lparam);
    }

    private static void Paint(IntPtr window, IntPtr device, int sequence)
    {
        GetClientRect(window, out var client);
        var red = CreateSolidBrush(_inverse ? 0x00ff0000u : 0x000000ffu);
        var blue = CreateSolidBrush(_inverse ? 0x000000ffu : 0x00ff0000u);
        var marker = CreateSolidBrush(0x0000ff00);
        try
        {
            FillRect(device, ref client, red);
            client.Right /= 2; FillRect(device, ref client, blue);
            if (sequence != 0)
                for (var bit = 0; bit < 16; bit++)
                {
                    if ((sequence & (1 << bit)) == 0) continue;
                    var square = new Rect { Left = 8 + bit * 8, Top = 8, Right = 14 + bit * 8, Bottom = 14 };
                    FillRect(device, ref square, marker);
                }
        }
        finally { DeleteObject(red); DeleteObject(blue); DeleteObject(marker); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        internal uint Size, Style; internal WindowProcedure Procedure; internal int ClassExtra, WindowExtra;
        internal IntPtr Instance, Icon, Cursor, Background; internal string? MenuName; internal string ClassName; internal IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    { internal IntPtr Window; internal uint Id; internal UIntPtr Wparam; internal IntPtr Lparam; internal uint Time; internal Point Point; internal uint Private; }
    [StructLayout(LayoutKind.Sequential)] private struct PaintState
    {
        internal IntPtr Device; [MarshalAs(UnmanagedType.Bool)] internal bool Erase; internal Rect Paint;
        [MarshalAs(UnmanagedType.Bool)] internal bool Restore; [MarshalAs(UnmanagedType.Bool)] internal bool IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] Reserved;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? module);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint extended, string className,
        string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "AdjustWindowRectEx")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AdjustWindowRect(
        ref Rect rectangle, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint extended);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Message message, IntPtr window, uint first, uint last);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr device, ref Rect rectangle, IntPtr brush);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr window, ref PaintState paint);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EndPaint(IntPtr window, ref PaintState paint);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr item);
}
