using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal static class DeviceForegroundFixture
{
    internal static async Task FocusAsync(ProviderRuntimeFixture fixture, Task running)
    {
        var path = fixture.Marker + ".window";
        var elapsed = Stopwatch.StartNew();
        JsonDocument? ready = null;
        while (ready is null && !running.IsCompleted && elapsed.Elapsed < TimeSpan.FromSeconds(20))
        {
            try
            {
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                ready = await JsonDocument.ParseAsync(input);
            }
            catch (Exception error) when (error is IOException or JsonException) { await Task.Delay(20); }
        }
        using var report = ready ?? throw new IOException("device_fixture_window_missing");
        var window = new nint(report.RootElement.GetProperty("windowHandle").GetInt64());
        var pid = report.RootElement.GetProperty("processId").GetInt32();
        using var process = Process.GetProcessById(pid);
        if (WindowProcess(window, out var owner) == 0 || owner != pid ||
            !string.Equals(process.MainModule!.FileName, fixture.Source.OtherImage, StringComparison.OrdinalIgnoreCase))
            throw new IOException("device_fixture_window_unbound");
        if (!GetCursorPos(out var saved)) throw new IOException("device_fixture_cursor_missing");
        try
        {
            var point = new Point { X = 320, Y = 240 };
            if (!SetWindowPos(window, new(-1), 0, 0, 0, 0, 0x43) || !ClientToScreen(window, ref point) || !SetCursorPos(point.X, point.Y))
                throw new IOException("device_fixture_position_failed");
            elapsed.Restart();
            while (WindowFromPoint(point) != window && elapsed.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
            if (WindowFromPoint(point) != window)
            {
                var covering = WindowFromPoint(point); _ = WindowProcess(covering, out var coveringPid);
                throw new IOException($"device_fixture_window_obscured: expected={window}/{pid}, actual={covering}/{coveringPid}, point={point.X},{point.Y}, exited={process.HasExited}");
            }
            // The controlled window receives a real activation event; no foreground permission is granted globally.
            if (SendInput(2, [new() { Flags = 2 }, new() { Flags = 4 }], Marshal.SizeOf<Input>()) != 2)
                throw new IOException("device_fixture_activation_failed");
            elapsed.Restart();
            while (GetForegroundWindow() != window && elapsed.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
            if (GetForegroundWindow() != window) throw new IOException("device_fixture_foreground_missing");
        }
        finally { _ = SetCursorPos(saved.X, saved.Y); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input
    { [FieldOffset(20)] internal uint Flags; }
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] private static extern uint WindowProcess(nint window, out uint pid);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
