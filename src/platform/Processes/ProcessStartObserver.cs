using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Processes;

[SupportedOSPlatform("windows")]
public sealed class ProcessStartObserver : IDisposable
{
    private readonly SafeKernelHandle _job;
    private readonly SafeKernelHandle _port;
    private readonly Thread _thread;
    private readonly Action<int, SafeKernelHandle> _observe;
    private readonly ConcurrentQueue<string> _failures = new();
    private readonly ManualResetEventSlim _drained = new();
    private readonly object _fence = new();
    private volatile bool _stopping;
    private int _disposed;

    internal ProcessStartObserver(SafeKernelHandle job, Action<int, SafeKernelHandle> observe)
    {
        _job = job;
        _observe = observe;
        _port = CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, UIntPtr.Zero, 1);
        if (_port.IsInvalid)
        {
            var failure = new Win32Exception(Marshal.GetLastPInvokeError());
            _port.Dispose();
            throw failure;
        }
        var association = new Association { Key = new IntPtr(1), Port = _port.DangerousGetHandle() };
        if (!SetInformationJobObject(job, 7, ref association, (uint)Marshal.SizeOf<Association>()))
        {
            var failure = new Win32Exception(Marshal.GetLastPInvokeError());
            _port.Dispose();
            throw failure;
        }
        _thread = new Thread(ReadNotifications) { IsBackground = true, Name = "Nexus owned Job start observer" };
        _thread.Start();
    }

    public string[] Failures => _failures.ToArray();

    public bool Drain(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (_fence)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _drained.Reset();
            if (!PostQueuedCompletionStatus(_port, uint.MaxValue, new UIntPtr(1), IntPtr.Zero))
            {
                _failures.Enqueue("completion_fence:" + Marshal.GetLastPInvokeError());
                return false;
            }
            return _drained.Wait(timeout);
        }
    }

    private void ReadNotifications()
    {
        while (!_stopping)
        {
            if (!GetQueuedCompletionStatus(_port, out var message, out var key, out var value, 250))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == 258) continue;
                if (!_stopping) _failures.Enqueue("completion_port:" + error);
                return;
            }
            if (_stopping) return;
            if (key != new UIntPtr(1)) continue;
            if (message == uint.MaxValue) { _drained.Set(); continue; }
            if (message != 6) continue;
            try
            {
                var pid = checked((int)value);
                using var process = JobNative.OpenProcess(0x101000, false, pid);
                if (process.IsInvalid || !JobNative.IsProcessInJob(process, _job, out var member))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                if (!member) throw new IOException("Notified PID no longer belongs to the owned Job");
                _observe(pid, process);
            }
            catch (Exception exception) { _failures.Enqueue("process_start:" + value + ":" + exception.GetType().Name); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping = true;
        var detach = new Association();
        if (!_job.IsClosed && !SetInformationJobObject(_job, 7, ref detach, (uint)Marshal.SizeOf<Association>()))
            _failures.Enqueue("completion_detach:" + Marshal.GetLastPInvokeError());
        if (Thread.CurrentThread != _thread && !_thread.Join(TimeSpan.FromSeconds(5)))
            _failures.Enqueue("completion_thread_stop_deadline");
        _port.Dispose();
        _drained.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Association { internal IntPtr Key, Port; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeKernelHandle CreateIoCompletionPort(IntPtr file, IntPtr existing, UIntPtr key, uint concurrency);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeKernelHandle job, int kind, ref Association association, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetQueuedCompletionStatus(SafeKernelHandle port, out uint bytes, out UIntPtr key, out IntPtr value, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostQueuedCompletionStatus(SafeKernelHandle port, uint bytes, UIntPtr key, IntPtr value);
}
