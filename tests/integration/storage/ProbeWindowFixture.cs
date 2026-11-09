using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class ProbeWindowFixture : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<(uint ThreadId, IntPtr Handle)> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly uint _threadId;
    private readonly ManualResetEventSlim _paused = new(), _resume = new();
    internal string Title { get; } = "Nexus probe " + Guid.NewGuid().ToString("N");
    internal IntPtr Handle { get; }

    internal ProbeWindowFixture()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Nexus probe window fixture" };
        _thread.Start();
        (_threadId, Handle) = _ready.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }

    private void Run()
    {
        IntPtr window = IntPtr.Zero;
        try
        {
            window = CreateWindowExW(0x08000080, "STATIC", Title, 0x10cf0000, 120, 120, 320, 160,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError());
            _ready.SetResult((GetCurrentThreadId(), window));
            int result;
            while ((result = GetMessageW(out var message, IntPtr.Zero, 0, 0)) > 0)
            {
                if (message.Kind == 0x8001)
                {
                    _paused.Set();
                    if (!_resume.Wait(TimeSpan.FromSeconds(15))) throw new IOException("probe_fixture_pause_timeout");
                    continue;
                }
                TranslateMessage(ref message); DispatchMessageW(ref message);
            }
            if (result < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            _closed.SetResult();
        }
        catch (Exception error) { _ready.TrySetException(error); _closed.TrySetException(error); }
        finally { if (window != IntPtr.Zero) DestroyWindow(window); }
    }

    public void Dispose()
    {
        _resume.Set();
        if (!PostThreadMessageW(_threadId, 0x12, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        _closed.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        if (!_thread.Join(TimeSpan.FromSeconds(10))) throw new IOException("probe_window_thread_not_stopped");
        _paused.Dispose(); _resume.Dispose();
    }

    internal void Pause()
    {
        if (!PostThreadMessageW(_threadId, 0x8001, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!_paused.Wait(TimeSpan.FromSeconds(10))) throw new IOException("probe_fixture_not_paused");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal IntPtr Window;
        internal uint Kind;
        internal UIntPtr WParam;
        internal IntPtr LParam;
        internal uint Time;
        internal int X, Y;
        internal uint Private;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessageW(out Message message, IntPtr window, uint minimum, uint maximum);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref Message message);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
