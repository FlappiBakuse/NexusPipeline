using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace NexusPipeline.Platform.Processes;

internal sealed record PreviewJobStop(string JobName, uint TotalProcesses, uint Flags);

[SupportedOSPlatform("windows")]
internal sealed class OwnedPreviewJob : IDisposable
{
    internal const uint LimitFlags = 0x00002208;
    internal const uint MaximumActiveProcesses = 64;
    internal const ulong MaximumMemoryBytes = 268435456;
    private readonly object _gate = new();
    private readonly SafeKernelHandle _job;
    private bool _closing, _disposed;
    internal string Name { get; }

    internal OwnedPreviewJob(string name)
    {
        if (name is not { Length: 50 } || !name.StartsWith("Local\\NexusWorker-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[18..], "N", out var id) || id == Guid.Empty || id.ToString("N") != name[18..]) throw Invalid();
        Name = name;
        _job = JobNative.CreateJobObjectW(0, Name);
        if (_job.IsInvalid) throw Invalid();
        if (Marshal.GetLastPInvokeError() == 183) { _job.Dispose(); throw Invalid(); }
        var limits = new JobNative.ExtendedLimits
        {
            Basic = new() { Flags = LimitFlags, ActiveProcessLimit = MaximumActiveProcesses },
            JobMemoryLimit = (nuint)MaximumMemoryBytes
        };
        if (!JobNative.SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<JobNative.ExtendedLimits>()))
        { _job.Dispose(); throw Invalid(); }
        try { RequireLimits(_job); }
        catch { _job.Dispose(); throw; }
    }

    internal T UseStart<T>(Func<T> start)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_closing) throw Invalid();
            RequireLimits(_job);
            return start();
        }
    }

    internal void AssignSuspended(SafeKernelHandle process)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_closing || !JobNative.AssignProcessToJobObject(_job, process) ||
                !JobNative.IsProcessInJob(process, _job, out var member) || !member) throw Invalid();
        }
    }

    internal T UseObservedMember<T>(ProcessIdentity identity, Func<T> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_closing) throw Invalid();
            RequireLimits(_job);
            using var process = Process.GetProcessById(identity.ProcessId);
            if (process.HasExited || process.StartTime.ToUniversalTime() != identity.CreatedAtUtc ||
                ProcessIdentityReader.Read(process.Id) != identity ||
                !JobNative.IsProcessInJob(process.SafeHandle, _job, out var member) || !member) throw Invalid();
            return action();
        }
    }

    internal async Task<PreviewJobStop> StopAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(60)) throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireLimits(_job); _closing = true;
            if (Accounting(_job).ActiveProcesses != 0 && !JobNative.TerminateJobObject(_job, 1)) throw Invalid();
        }
        var clock = Stopwatch.StartNew();
        while (true)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                RequireLimits(_job);
                var state = Accounting(_job);
                if (state.ActiveProcesses == 0 && Accounting(_job).ActiveProcesses == 0)
                    return new(Name, state.TotalProcesses, LimitFlags);
            }
            if (clock.Elapsed >= timeout) throw new IOException("preview_job_stop_unconfirmed");
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    internal PreviewJobStop RequireStopped()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); RequireLimits(_job);
            var state = Accounting(_job);
            if (!_closing || state.ActiveProcesses != 0 || Accounting(_job).ActiveProcesses != 0) throw Invalid();
            return new(Name, state.TotalProcesses, LimitFlags);
        }
    }

    internal static bool ObserveStopped(string name, int sessionId)
    {
        using var process = Process.GetCurrentProcess();
        if (sessionId != process.SessionId) return false;
        using var job = JobNative.OpenJobObjectW(4, false, name);
        if (job.IsInvalid) return Marshal.GetLastPInvokeError() == 2;
        RequireLimits(job);
        return Accounting(job).ActiveProcesses == 0 && Accounting(job).ActiveProcesses == 0;
    }

    private static JobNative.Accounting Accounting(SafeKernelHandle job)
    {
        var size = Marshal.SizeOf<JobNative.Accounting>(); var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!JobNative.QueryInformationJobObject(job, 1, buffer, (uint)size, 0)) throw Invalid();
            return Marshal.PtrToStructure<JobNative.Accounting>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void RequireLimits(SafeKernelHandle job)
    {
        var size = Marshal.SizeOf<JobNative.ExtendedLimits>(); var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!JobNative.QueryInformationJobObject(job, 9, buffer, (uint)size, 0)) throw Invalid();
            var value = Marshal.PtrToStructure<JobNative.ExtendedLimits>(buffer);
            if (value.Basic.Flags != LimitFlags || value.Basic.ActiveProcessLimit != MaximumActiveProcesses ||
                value.JobMemoryLimit.ToUInt64() != MaximumMemoryBytes) throw Invalid();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_closing && Accounting(_job).TotalProcesses == 0) _closing = true;
            _ = RequireStopped(); _job.Dispose(); _disposed = true;
        }
    }
    private static IOException Invalid() => new("preview_job_native_unbound");
}
