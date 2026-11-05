using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class PcDeviceInput(PcTargetStage target, Func<bool> expired, string mouseMethod = "sendMessage", string keyboardMethod = "sendMessage")
{
    private readonly HashSet<int> _keys = [];
    private readonly PcDesktopInput _desktop = new();
    private PreparedPcWindow? _window;
    private bool _closed, _pointerDown, _positioned;
    private long _x, _y;

    internal void Present(CancellationToken cancellation)
    {
        RequireInput(cancellation);
        _window ??= target.PrepareWindow();
        _window.Use(facts =>
        {
            RequireInput(cancellation);
            var window = new IntPtr(facts.WindowHandle);
            if (!SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, 0x13)) throw new IOException("execution_device_present_failed");
            _ = SetForegroundWindow(window);
            return true;
        });
    }

    internal async ValueTask<bool> InvokeAsync(ExecutionDeviceRequest request, CancellationToken cancellation)
    {
        RequireInput(cancellation);
        _window ??= target.PrepareWindow();
        if (request.Kind == "swipe") return await SwipeAsync(request, cancellation).ConfigureAwait(false);
        if (request.Kind == "touch" && request.Phase == 2)
        { if (request.Contact != 0) return false; ReleasePointer(); return true; }
        if (request.Kind == "text")
        {
            foreach (var character in request.Text!)
            { _window.Use(facts => { RequireInput(cancellation); Send(facts, 0x102, character, 1); return true; }); }
            return true;
        }
        return _window.Use(facts =>
        {
            RequireInput(cancellation);
            if (request.Kind is "connect" or "connected") return true;
            if (request.Kind == "inactive") { ReleasePointer(); _desktop.Restore(); return true; }
            if (request.Kind == "click")
            {
                var point = Position(facts, request.X!.Value, request.Y!.Value);
                try { _pointerDown = true; Send(facts, 0x201, 1, point); return true; }
                finally { ReleasePointer(); }
            }
            if (request.Kind == "key") return Key(facts, checked((int)request.KeyCode!.Value), request.Phase!.Value);
            if (request.Kind == "touch")
            {
                if (request.Contact != 0) return false;
                var point = Position(facts, request.X!.Value, request.Y!.Value);
                if (request.Phase == 0) { _pointerDown = true; Send(facts, 0x201, 1, point); }
                else if (_pointerDown) Send(facts, 0x200, 1, point);
                else return false;
                return true;
            }
            if (request.Kind == "move")
            {
                if (!_positioned) return false;
                Send(facts, 0x200, _pointerDown ? 1 : 0, Position(facts, _x + request.Dx!.Value, _y + request.Dy!.Value)); return true;
            }
            if (request.Kind == "scroll")
            {
                if (mouseMethod is "seize" or "sendMessageWithCursorPos")
                    _desktop.Position(facts, _positioned ? _x : facts.Width / 2, _positioned ? _y : facts.Height / 2, mouseMethod == "seize");
                var point = new NativePoint();
                if (!ClientToScreen(new IntPtr(facts.WindowHandle), ref point)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                var location = (long)(ushort)point.X | (long)(ushort)point.Y << 16;
                try
                {
                    if (request.Dx != 0) Send(facts, 0x20e, (long)(ushort)request.Dx!.Value << 16, location);
                    if (request.Dy != 0) Send(facts, 0x20a, (long)(ushort)request.Dy!.Value << 16, location);
                }
                finally { if (!_pointerDown) _desktop.Restore(); }
                return true;
            }
            return false;
        });
    }

    private bool Key(PcWindowFacts facts, int code, long phase)
    {
        if (code > 255) return false;
        if (phase == 0)
        { try { Send(facts, 0x100, code, KeyParameter(code, false)); return true; } finally { ReleaseKey(facts, code); } }
        if (phase == 1) { _keys.Add(code); Send(facts, 0x100, code, KeyParameter(code, false)); }
        else { if (!_keys.Contains(code)) return false; ReleaseKey(facts, code); _keys.Remove(code); }
        return true;
    }
    private async ValueTask<bool> SwipeAsync(ExecutionDeviceRequest request, CancellationToken cancellation)
    {
        var window = _window!; var duration = request.DurationMilliseconds!.Value;
        window.Use(facts => { RequireInput(cancellation); _ = Point(facts, request.X2!.Value, request.Y2!.Value);
            var point = Position(facts, request.X!.Value, request.Y!.Value); _pointerDown = true; Send(facts, 0x201, 1, point); return true; });
        var timer = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                RequireInput(cancellation);
                var ratio = duration == 0 ? 1 : Math.Min(1, timer.Elapsed.TotalMilliseconds / duration);
                var x = request.X!.Value + (long)Math.Round((request.X2!.Value - request.X.Value) * ratio);
                var y = request.Y!.Value + (long)Math.Round((request.Y2!.Value - request.Y.Value) * ratio);
                window.Use(facts => { RequireInput(cancellation); Send(facts, 0x200, 1, Position(facts, x, y)); return true; });
                if (ratio >= 1) break;
                await Task.Delay(10, cancellation).ConfigureAwait(false);
            }
            return true;
        }
        finally { ReleasePointer(); }
    }
    private long Position(PcWindowFacts facts, long x, long y)
    {
        var point = Point(facts, x, y);
        if (mouseMethod is "seize" or "sendMessageWithCursorPos") _desktop.Position(facts, x, y, mouseMethod == "seize");
        _x = x; _y = y; _positioned = true; return point;
    }
    private void RequireInput(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (_closed || expired()) throw new IOException("execution_device_input_closed");
    }
    private static long Point(PcWindowFacts facts, long x, long y)
    {
        if (x < 0 || y < 0 || x >= facts.Width || y >= facts.Height) throw new IOException("execution_device_coordinates_invalid");
        return x | y << 16;
    }
    private void ReleasePointer()
    {
        if (!_pointerDown || _window is null) return;
        try
        {
            if (mouseMethod == "seize") PcDesktopInput.ReleasePointer();
            else target.ReleaseInput(_window, [], _x | _y << 16, mouseMethod, keyboardMethod);
            _pointerDown = false;
        }
        finally { _desktop.Restore(); }
    }
    private void ReleaseKey(PcWindowFacts facts, int code)
    {
        if (keyboardMethod == "seize") PcDesktopInput.ReleaseKey(code);
        else Send(facts, 0x101, code, KeyParameter(code, true));
    }
    internal void Close()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            try
            {
                if (keyboardMethod == "seize") foreach (var key in _keys) PcDesktopInput.ReleaseKey(key);
                else if (_window is not null && _keys.Count > 0) target.ReleaseInput(_window, _keys.ToArray(), null, mouseMethod, keyboardMethod);
            }
            finally { ReleasePointer(); }
            _keys.Clear();
        }
        finally { try { _desktop.Restore(); } finally { _window?.Dispose(); _window = null; } }
    }
    internal static void Release(PcWindowFacts facts, IReadOnlyList<int> keys, long? pointer, string mouseMethod = "sendMessage", string keyboardMethod = "sendMessage")
    {
        if (WindowProcessId(new IntPtr(facts.WindowHandle), out var pid) == 0 || pid != facts.Process.ProcessId)
            throw new IOException("execution_device_release_unbound");
        foreach (var code in keys) SendNative(facts, 0x101, code, KeyParameter(code, true), keyboardMethod);
        if (pointer is { } point) SendNative(facts, 0x202, 0, point, mouseMethod);
    }
    private static long KeyParameter(int code, bool up) => 1L | (long)KeyScan(checked((uint)code), 0) << 16 | (up ? 0xc0000000L : 0);
    private void Send(PcWindowFacts facts, uint message, long value, long parameter) =>
        SendNative(facts, message, value, parameter, message is 0x100 or 0x101 or 0x102 ? keyboardMethod : mouseMethod);
    private static void SendNative(PcWindowFacts facts, uint message, long value, long parameter, string method)
    {
        if (method == "seize") { PcDesktopInput.Send(facts, message, value); return; }
        if (method == "postMessage")
        {
            if (!PostMessage(new IntPtr(facts.WindowHandle), message, new UIntPtr(unchecked((ulong)value)), new IntPtr(parameter)))
                throw new IOException("execution_device_post_failed");
            return;
        }
        if (method is not ("sendMessage" or "sendMessageWithCursorPos")) throw new IOException("execution_device_input_method_invalid");
        if (SendTimed(new IntPtr(facts.WindowHandle), message, new UIntPtr(unchecked((ulong)value)), new IntPtr(parameter), 0x22, 100, out _) == IntPtr.Zero)
            throw new IOException("execution_device_window_unresponsive");
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(
        IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, UIntPtr value, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SendTimed(IntPtr window, uint message, UIntPtr value, IntPtr parameter, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")] private static extern uint KeyScan(uint code, uint mode);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] private static extern uint WindowProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
}
