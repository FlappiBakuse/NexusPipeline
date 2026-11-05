using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class CaptureChildLease : IDisposable
{
    private readonly OwnedWorkerJob _inner;
    private readonly Process _process;
    private readonly SafeKernelHandle _job;
    private bool _disposed;
    internal ExecutionCaptureBinding Binding { get; }
    internal int Sequence { get; }
    internal string JobName { get; }
    internal ProcessIdentity ProcessIdentity { get; }

    internal CaptureChildLease(OwnedWorkerJob actualInner, ExecutionCaptureBinding binding,
        int sequence, string jobName, ProcessIdentity child)
    {
        Binding = binding; Sequence = sequence; JobName = jobName; ProcessIdentity = child; _inner = actualInner;
        if (actualInner.Name != binding.HelperJobName || sequence is < 1 or > 3 ||
            !ExecutionCaptureCodec.JobName(jobName) || jobName == binding.HelperJobName || jobName == binding.Primary.JobName)
            throw Invalid();
        _process = Process.GetProcessById(child.ProcessId);
        try
        {
            _job = JobNative.OpenJobObjectW(4, false, jobName);
            try { RequireAwaitingMember(actualInner); }
            catch { _job.Dispose(); throw; }
        }
        catch { _process.Dispose(); throw; }
    }

    internal void RequireAwaitingMember(OwnedWorkerJob actualInner)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(actualInner, _inner) || _job.IsInvalid ||
            _process.SessionId != Binding.Primary.SessionId || _process.HasExited ||
            _process.StartTime.ToUniversalTime() != ProcessIdentity.CreatedAtUtc ||
            ProcessIdentityReader.Read(_process.Id) != ProcessIdentity ||
            !actualInner.ContainsProcessHandle(_process.SafeHandle) ||
            !JobNative.IsProcessInJob(_process.SafeHandle, _job, out var member) || !member ||
            Accounting().TotalProcesses == 0 || Flags() != 0x00002000) throw Invalid();
    }

    internal void RequireObserved(CaptureCommandObserved observed)
    {
        if (observed.Binding != Binding) throw Invalid();
        RequireObserved(observed.ExitCode, observed.Completion, observed.Stopped);
    }

    internal void RequireObserved(int? exitCode, string completion, OwnedTargetInventory stopped)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var accounting = Accounting();
        if (!_process.HasExited || exitCode is { } code && _process.ExitCode != code ||
            accounting.ActiveProcesses != 0 || accounting.TotalProcesses != stopped.TotalProcesses ||
            Flags() != 0x00002000) throw Invalid();
        TargetJobEvidence.RequireInventory(stopped);
        if (!stopped.AllFamily.Contains(ProcessIdentity) ||
            completion == "natural-root-exit-and-eof-before-stop" && exitCode is null) throw Invalid();
    }

    private JobNative.Accounting Accounting()
    {
        var size = Marshal.SizeOf<JobNative.Accounting>(); var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!JobNative.QueryInformationJobObject(_job, 1, buffer, (uint)size, IntPtr.Zero)) throw Invalid();
            return Marshal.PtrToStructure<JobNative.Accounting>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private uint Flags()
    {
        var size = Marshal.SizeOf<JobNative.ExtendedLimits>(); var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!JobNative.QueryInformationJobObject(_job, 9, buffer, (uint)size, IntPtr.Zero)) throw Invalid();
            return Marshal.PtrToStructure<JobNative.ExtendedLimits>(buffer).Basic.Flags;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _job.Dispose(); _process.Dispose();
    }

    private static IOException Invalid() => new("execution_capture_child_native_unbound");
}
