using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class MaintenanceParentExit : IDisposable
{
    private const uint Continue = 0x00010002;
    private const uint ExceptionNotHandled = 0x80010001;
    private readonly TaskCompletionSource _attached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _exitPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _release = new();
    private readonly Thread _debugger;
    private Exception? _failure;

    internal MaintenanceParentExit(Process parent)
    {
        _debugger = new(() => HoldExit(parent.Id)) { IsBackground = true };
        _debugger.Start();
    }

    internal Task Attached => _attached.Task;
    internal Task ExitPending => _exitPending.Task;

    internal static bool IsSignaled(Process process) => WaitForSingleObject(process.SafeHandle, 0) switch
    {
        0 => true,
        258 => false,
        _ => throw NativeFailure()
    };

    private void HoldExit(int processId)
    {
        var attached = false;
        try
        {
            Require(DebugActiveProcess(processId));
            attached = true;
            Require(DebugSetProcessKillOnExit(false));
            while (!_release.IsSet)
            {
                if (!WaitForDebugEvent(out var next, 250))
                {
                    if (Marshal.GetLastPInvokeError() == 121) continue;
                    throw NativeFailure();
                }

                var initialBreak = next.Kind == 1 && unchecked((uint)next.FirstField.ToInt64()) == 0x80000003 &&
                    !_attached.Task.IsCompleted;
                var exit = next.Kind == 5;
                try
                {
                    if (next.Kind is 3 or 6 && next.FirstField != 0) Require(CloseHandle(next.FirstField));
                    if (exit)
                    {
                        // Windows signals the process handle after the debugger continues this event.
                        _exitPending.TrySetResult();
                        _release.Wait();
                    }
                }
                finally
                {
                    Require(ContinueDebugEvent(next.ProcessId, next.ThreadId,
                        next.Kind == 1 && !initialBreak ? ExceptionNotHandled : Continue));
                }
                if (initialBreak) _attached.TrySetResult();
                if (exit) { attached = false; return; }
            }
        }
        catch (Exception error)
        {
            _failure = error;
            _attached.TrySetException(error);
            _exitPending.TrySetException(error);
        }
        finally
        {
            if (attached && !DebugActiveProcessStop(processId)) _failure ??= NativeFailure();
        }
    }

    public void Dispose()
    {
        _release.Set();
        if (!_debugger.Join(TimeSpan.FromSeconds(10))) throw new IOException("maintenance_test_debugger_still_running");
        _release.Dispose();
        if (_failure is { } failure) throw new IOException("maintenance_test_debugger_failed", failure);
    }

    private static void Require(bool succeeded)
    {
        if (!succeeded) throw NativeFailure();
    }

    private static Win32Exception NativeFailure() => new(Marshal.GetLastPInvokeError());

    [StructLayout(LayoutKind.Sequential)]
    private struct DebugEvent
    {
        internal uint Kind;
        internal uint ProcessId;
        internal uint ThreadId;
        private DebugEventInfo _info;
        internal readonly nint FirstField => _info.FirstField;
    }

    [StructLayout(LayoutKind.Explicit, Size = 160)]
    private struct DebugEventInfo
    {
        [FieldOffset(0)] internal nint FirstField;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DebugActiveProcess(int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DebugActiveProcessStop(int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DebugSetProcessKillOnExit(bool killOnExit);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WaitForDebugEvent(out DebugEvent debugEvent, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ContinueDebugEvent(uint processId, uint threadId, uint status);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
}
