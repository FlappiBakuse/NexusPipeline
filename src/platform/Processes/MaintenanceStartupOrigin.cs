using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Networking;
using System.Buffers.Binary;
using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;

namespace NexusPipeline.Platform.Processes;

[SupportedOSPlatform("windows")]
internal sealed class MaintenanceStartupOrigin : IDisposable
{
    private readonly SafeKernelHandle _process;
    private readonly ProcessIdentity _identity;
    private readonly MaintenanceJobQuery? _currentJob;
    private bool _disposed;

    private MaintenanceStartupOrigin(SafeKernelHandle process, ProcessIdentity identity, MaintenanceJobQuery? currentJob = null)
    {
        _process = process;
        _identity = identity;
        _currentJob = currentJob;
    }

    internal static MaintenanceStartupOrigin CaptureUnjobbedProcess()
    {
        var process = JobNative.OpenProcess(0x101000, false, Environment.ProcessId);
        if (process.IsInvalid) { process.Dispose(); throw Failure(); }
        try
        {
            var result = new MaintenanceStartupOrigin(process, ProcessIdentityReader.Read(process, Environment.ProcessId));
            result.RequireCurrent();
            return result;
        }
        catch { process.Dispose(); throw; }
    }

    internal void RequireCurrent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_identity.ProcessId != Environment.ProcessId || JobNative.WaitForSingleObject(_process, 0) != 258 ||
            ProcessIdentityReader.Read(_process, Environment.ProcessId) != _identity) throw Invalid();
        if (_currentJob is null)
        {
            if (!IsProcessInAnyJob(_process, IntPtr.Zero, out var member)) throw Failure();
            if (member) throw Invalid();
        }
        else
        {
            _currentJob.RequireMember(_identity);
            if (_currentJob.Flags is not (0x800 or 0x2800)) throw Invalid();
        }
    }

    internal static async Task<MaintenanceStartupOrigin> ReceiveSuccessorAsync(NamedPipeClientStream pipe,
        NativeClientBinding actualParent, ProcessIdentity expectedParent, long sourceHandle, CancellationToken cancellation)
    {
        RequirePeer();
        using var received = MaintenanceJobQuery.Receive(actualParent, expectedParent, sourceHandle);
        received.RequireMember(ProcessIdentityReader.Read(Environment.ProcessId));
        if (received.Flags != 0x2800) throw Invalid();
        var reply = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(reply, received.ExportValue);
        await pipe.WriteAsync(reply, cancellation).ConfigureAwait(false);
        await pipe.FlushAsync(cancellation).ConfigureAwait(false);
        var acknowledgement = new byte[1];
        await pipe.ReadExactlyAsync(acknowledgement, cancellation).ConfigureAwait(false);
        RequirePeer();
        if (acknowledgement[0] != 1) throw Invalid();
        return CaptureAcknowledgedSuccessor(received, actualParent, expectedParent);

        void RequirePeer()
        {
            if (!actualParent.IsRegistered(expectedParent) ||
                !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var producer) || producer != expectedParent.ProcessId)
                throw Invalid();
        }
    }

    internal static async Task AcknowledgeSuccessorAsync(NamedPipeServerStream pipe, MaintenanceStartupJob actualJob,
        MaintenanceJobQuery offeredJob, NativeClientBinding actualChild, ProcessIdentity expectedChild, CancellationToken cancellation)
    {
        RequirePeer();
        var echo = new byte[8]; await pipe.ReadExactlyAsync(echo, cancellation).ConfigureAwait(false);
        using var received = MaintenanceJobQuery.Receive(actualChild, expectedChild, BinaryPrimitives.ReadInt64LittleEndian(echo));
        offeredJob.RequireSameObject(received);
        using var current = actualJob.RetainSuccessorJob(expectedChild);
        offeredJob.RequireSameObject(current);
        RequirePeer();
        await pipe.WriteAsync(new byte[] { 1 }, cancellation).ConfigureAwait(false);
        await pipe.FlushAsync(cancellation).ConfigureAwait(false);

        void RequirePeer()
        {
            if (!actualChild.IsRegistered(expectedChild) ||
                !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var child) || child != expectedChild.ProcessId)
                throw Invalid();
        }
    }

    private static MaintenanceStartupOrigin CaptureAcknowledgedSuccessor(MaintenanceJobQuery currentJob,
        NativeClientBinding actualParent, ProcessIdentity expectedParent)
    {
        if (!actualParent.IsRegistered(expectedParent)) throw Invalid();
        var process = JobNative.OpenProcess(0x101000, false, Environment.ProcessId);
        MaintenanceJobQuery? held = null;
        try
        {
            if (process.IsInvalid) throw Failure();
            held = currentJob.Clone();
            var result = new MaintenanceStartupOrigin(process, ProcessIdentityReader.Read(process, Environment.ProcessId), held);
            result.RequireCurrent();
            if (held.Flags != 0x2800 || !actualParent.IsRegistered(expectedParent)) throw Invalid();
            return result;
        }
        catch { held?.Dispose(); process.Dispose(); throw; }
    }

    internal void RequireChildExcluded(SafeKernelHandle actualChild)
    {
        RequireCurrent();
        _currentJob?.RequireExcluded(actualChild);
    }

    internal void RequireReleasedSuccessor()
    {
        RequireCurrent();
        if (_currentJob is null || _currentJob.Flags != 0x800) throw Invalid();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _currentJob?.Dispose(); } finally { _process.Dispose(); }
    }

    private static IOException Invalid() => new("maintenance_startup_origin_has_unknown_job");
    private static Win32Exception Failure() => new(Marshal.GetLastPInvokeError());

    [DllImport("kernel32.dll", EntryPoint = "IsProcessInJob", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInAnyJob(SafeKernelHandle process, IntPtr job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int processId);
}
