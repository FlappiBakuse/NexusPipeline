using System.ComponentModel;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Platform.Storage;
using Microsoft.Win32.SafeHandles;

namespace NexusPipeline.Platform.Processes;

public sealed record OwnedWorkerIdentity(int ProcessId, DateTime CreatedAtUtc, string ImagePath,
    string ImageSha256, string UserSid, bool IsElevated, Guid InstanceId, Guid AttemptId);

public sealed record WorkerJobAccounting(double CpuSeconds, uint TotalProcesses, uint ActiveProcesses);
public sealed record JobStopObservation(string FailureCode, uint InitialTotalProcesses, uint FinalTotalProcesses,
    uint ActiveProcesses, bool InventoryComplete, bool HandlesSignaled);
internal interface IJobStopObserver
{
    void InventoryCaptured(IReadOnlyList<int> processIds);
}

[SupportedOSPlatform("windows")]
public sealed class OwnedWorkerJob : IDisposable
{
    private readonly SafeKernelHandle _job;
    private readonly JobTargetLifetime _targetLifetime;
    private readonly List<SafeKernelHandle> _processHandles = [];
    private readonly Dictionary<(int ProcessId, long Created), SafeKernelHandle> _observedHandles = [];
    private readonly Dictionary<(int ProcessId, long Created), ProcessIdentity> _observedIdentities = [];
    private readonly object _handleGate = new();
    private readonly Dictionary<OwnedWorkerIdentity, SafeKernelHandle> _roots = [];
    private bool _isDisposed;
    private ProcessStartObserver? _observer;
    private Action<int, long>? _reserveObservedIdentity;
    private readonly IJobStopObserver? _stopObserver;
    public string Name { get; } = "Local\\NexusWorker-" + Guid.NewGuid().ToString("N");
    public JobStopObservation? LastStopObservation { get; private set; }

    public OwnedWorkerJob()
    {
        _job = JobNative.CreateJobObjectW(IntPtr.Zero, Name);
        if (_job.IsInvalid)
            throw NativeFailure();
        if (Marshal.GetLastPInvokeError() == 183)
        {
            _job.Dispose();
            throw new IOException("worker_job_identity_already_exists");
        }
        var limits = new JobNative.ExtendedLimits
        {
            Basic = new JobNative.BasicLimits { Flags = 0x00002000 }
        };
        if (!JobNative.SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<JobNative.ExtendedLimits>()))
        {
            var error = NativeFailure();
            _job.Dispose();
            throw error;
        }
        _targetLifetime = new(this, _job);
    }

    internal OwnedWorkerJob(IJobStopObserver stopObserver) : this() => _stopObserver = stopObserver;

    public ProcessStartObserver ObserveProcessStarts(Action<int> observe)
    {
        ArgumentNullException.ThrowIfNull(observe);
        return ObserveProcessHandles((pid, _) => observe(pid));
    }

    internal ProcessStartObserver ObserveProcessIdentities(Action<ProcessIdentity> observe,
        Action<int, long>? reserveIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(observe);
        if (_observer is not null || GetAccounting().TotalProcesses != 0)
            throw new InvalidOperationException("Process observation must be attached once before the first process is assigned");
        _reserveObservedIdentity = reserveIdentity;
        return ObserveProcessHandles((pid, process) => observe(ObserveIdentity(process, pid)));
    }

    private ProcessStartObserver ObserveProcessHandles(Action<int, SafeKernelHandle> observe)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_observer is not null || GetAccounting().TotalProcesses != 0)
            throw new InvalidOperationException("Process observation must be attached once before the first process is assigned");
        return _observer = new ProcessStartObserver(_job, (pid, process) =>
        {
            RetainObservedProcess(pid, process);
            observe(pid, process);
        });
    }

    private void RetainObservedProcess(int pid, SafeKernelHandle process)
    {
        if (!JobNative.GetProcessTimes(process, out var created, out _, out _, out _)) throw NativeFailure();
        _reserveObservedIdentity?.Invoke(pid, created);
        if (!JobNative.DuplicateHandle(new IntPtr(-1), process, new IntPtr(-1), out var retained, 0, false, 2))
            throw NativeFailure();
        lock (_handleGate)
        {
            if (_isDisposed || _observedHandles.ContainsKey((pid, created))) { retained.Dispose(); return; }
            if (_observedHandles.Count >= 4096) { retained.Dispose(); throw new IOException("worker_observed_family_limit"); }
            // A later PID lookup cannot recover the kernel object once a short-lived member has exited.
            _observedHandles.Add((pid, created), retained);
        }
    }

    private SafeKernelHandle[] RetainedProcessHandles()
    {
        lock (_handleGate) return _processHandles.Concat(_observedHandles.Values).ToArray();
    }

    public OwnedWorkerIdentity Start(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        Guid instanceId, Guid attemptId, Action<OwnedWorkerIdentity> persistBeforeExecution, OwnedProcessOutput? output = null)
        => StartCore(executable, arguments, workingDirectory, instanceId, attemptId, persistBeforeExecution, output, CommandLineSyntax.ArgumentVector);

    internal OwnedWorkerIdentity StartDesktop(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        Guid instanceId, Guid launchId, Action<OwnedWorkerIdentity>? beforeStart = null, ProcessIdentityLease? imageProtection = null)
    {
        using var environment = MaintenanceStartupEnvironment.Freeze(MaintenanceStartupRole.DesktopRoot);
        return _targetLifetime.UseOwned(() => StartOwnedCore(executable, arguments, workingDirectory, instanceId,
            launchId, beforeStart ?? (_ => { }), null, CommandLineSyntax.ArgumentVector, frozenEnvironment: environment, imageProtection: imageProtection));
    }

    internal OwnedWorkerIdentity StartFrozen(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        Guid instanceId, Guid attemptId, Action<OwnedWorkerIdentity> persistBeforeExecution, OwnedProcessOutput? output, CommandLineSyntax syntax)
        => StartCore(executable, arguments, workingDirectory, instanceId, attemptId, persistBeforeExecution, output, syntax);

    internal OwnedWorkerIdentity StartNested(OwnedWorkerJob actualParent, string executable, IReadOnlyList<string> arguments,
        string workingDirectory, Guid instanceId, Guid attemptId, Action<OwnedWorkerIdentity> persistBeforeExecution,
        OwnedProcessOutput output)
    {
        ArgumentNullException.ThrowIfNull(actualParent);
        if (ReferenceEquals(actualParent, this)) throw new IOException("worker_nested_owner_invalid");
        return actualParent._targetLifetime.UseOwned(() => _targetLifetime.UseOwned(() =>
            StartOwnedCore(executable, arguments, workingDirectory, instanceId, attemptId, persistBeforeExecution,
                output, CommandLineSyntax.ArgumentVector, actualParent)));
    }

    internal OwnedWorkerIdentity StartPreview(OwnedPreviewJob domain, string executable, IReadOnlyList<string> arguments,
        string workingDirectory, Guid instanceId, Guid attemptId, Action<OwnedWorkerIdentity> persistBeforeExecution, OwnedProcessOutput output)
        => domain.UseStart(() => _targetLifetime.UseOwned(() => StartOwnedCore(executable, arguments, workingDirectory,
            instanceId, attemptId, persistBeforeExecution, output, CommandLineSyntax.ArgumentVector, actualPreview: domain)));

    private OwnedWorkerIdentity StartCore(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        Guid instanceId, Guid attemptId, Action<OwnedWorkerIdentity> persistBeforeExecution, OwnedProcessOutput? output, CommandLineSyntax syntax)
        => _targetLifetime.UseOwned(() => StartOwnedCore(executable, arguments, workingDirectory,
            instanceId, attemptId, persistBeforeExecution, output, syntax));

    private OwnedWorkerIdentity StartOwnedCore(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        Guid instanceId, Guid attemptId, Action<OwnedWorkerIdentity> persistBeforeExecution, OwnedProcessOutput? output,
        CommandLineSyntax syntax, OwnedWorkerJob? actualParent = null, OwnedPreviewJob? actualPreview = null,
        MaintenanceStartupEnvironment? frozenEnvironment = null, ProcessIdentityLease? imageProtection = null)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentNullException.ThrowIfNull(persistBeforeExecution);
        if (instanceId == Guid.Empty || attemptId == Guid.Empty)
            throw new ArgumentException("Worker instance and attempt identities are required");
        var image = Path.GetFullPath(executable);
        var directory = Path.GetFullPath(workingDirectory);
        if (!File.Exists(image) || !Directory.Exists(directory))
            throw new IOException("Worker input is missing");
        PhysicalPathPolicy.NormalizeWithoutLinks(image);
        PhysicalPathPolicy.NormalizeWithoutLinks(directory);
        var imageHash = imageProtection?.Sha256 ?? HashFile(image);
        var commandLine = WindowsCommandLine.Build(image, arguments, syntax);
        if (commandLine.Length >= 32767)
            throw new ArgumentException("Worker command line is too long");
        var startup = new JobNative.StartupInfo { Size = (uint)Marshal.SizeOf<JobNative.StartupInfo>() };
        // Windows PowerShell's CLR bootstrap rejects an extended image spelling even for a short path.
        var nativeImage = image.Length < 260 ? image : PhysicalPathPolicy.NativePath(image);
        JobNative.ProcessInformation created;
        if (!(output is null ? JobNative.CreateProcessW(nativeImage, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0x08000004u | (frozenEnvironment is null ? 0u : 0x400u),
                frozenEnvironment?.Pointer ?? IntPtr.Zero, directory, ref startup, out created) : output.Create(nativeImage, commandLine, directory, out created)))
            throw NativeFailure();
        using var thread = new SafeKernelHandle(created.Thread);
        var process = new SafeKernelHandle(created.Process);
        var retained = false;
        try
        {
            if (actualParent is not null)
            {
                // Root-first assignment keeps the helper and its descendants in the existing writer's recovery family.
                if (!JobNative.AssignProcessToJobObject(actualParent._job, process)) throw NativeFailure();
                actualParent.RetainObservedProcess(checked((int)created.ProcessId), process);
                _ = actualParent.ObserveIdentity(process, checked((int)created.ProcessId));
            }
            actualPreview?.AssignSuspended(process);
            if (!JobNative.AssignProcessToJobObject(_job, process))
                throw NativeFailure();
            ReserveObservedIdentity(process, checked((int)created.ProcessId));
            // Failed registration still leaves an owned suspended process whose original handle is needed for stop proof.
            lock (_handleGate) { _processHandles.Add(process); retained = true; }
            var identity = ReadIdentity(process, checked((int)created.ProcessId), instanceId, attemptId, imageProtection);
            if (!string.Equals(identity.ImagePath, image, StringComparison.OrdinalIgnoreCase) || identity.ImageSha256 != imageHash)
                throw new IOException("Started worker image identity differs from its frozen input");
            persistBeforeExecution(identity);
            if (JobNative.ResumeThread(thread) != 1)
                throw NativeFailure();
            _roots.Add(identity, process);
            return identity;
        }
        catch
        {
            JobNative.TerminateProcess(process, 1);
            if (!retained) process.Dispose();
            throw;
        }
    }

    public async Task<int?> AwaitRootAsync(OwnedWorkerIdentity identity, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromDays(7)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (!_roots.TryGetValue(identity, out var handle)) throw new IOException("worker_root_not_owned");
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = JobNative.WaitForSingleObject(handle, 0);
            if (result == 0)
            {
                if (!JobNative.GetExitCodeProcess(handle, out var code)) throw NativeFailure();
                return unchecked((int)code);
            }
            if (result != 258) throw NativeFailure();
            if (elapsed.Elapsed >= timeout) return null;
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
        }
    }

    internal int? ReadExitCode(OwnedWorkerIdentity identity)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (!_roots.TryGetValue(identity, out var handle)) throw new IOException("worker_root_not_owned");
        var result = JobNative.WaitForSingleObject(handle, 0);
        if (result == 258) return null;
        if (result != 0 || !JobNative.GetExitCodeProcess(handle, out var code)) throw NativeFailure();
        return unchecked((int)code);
    }

    internal T UseExitedRoot<T>(OwnedWorkerIdentity identity, Func<int, T> callback) => _targetLifetime.UseOwned(() =>
    {
        ArgumentNullException.ThrowIfNull(callback);
        var code = ReadExitCode(identity) ?? throw new IOException("worker_root_still_running");
        return callback(code);
    });


    internal T UseObservedMember<T>(ProcessIdentity identity, Func<T> action) => _targetLifetime.UseOwned(() =>
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(action);
        if (_observer is null || !_observer.Drain(TimeSpan.FromSeconds(5)) || _observer.Failures.Length != 0)
            throw new IOException("worker_member_identity_unconfirmed");
        using var process = Process.GetProcessById(identity.ProcessId);
        if (process.HasExited || process.StartTime.ToUniversalTime() != identity.CreatedAtUtc ||
            !ContainsProcessHandle(process.SafeHandle)) throw new IOException("worker_member_identity_unbound");
        lock (_handleGate)
            if (!_observedIdentities.TryGetValue((identity.ProcessId, identity.CreatedAtUtc.ToFileTimeUtc()), out var observed) ||
                observed != identity || !_observedHandles.ContainsKey((identity.ProcessId, identity.CreatedAtUtc.ToFileTimeUtc())))
                throw new IOException("worker_member_identity_unconfirmed");
        return action();
    });

    public bool ContainsProcessHandle(SafeProcessHandle process)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentNullException.ThrowIfNull(process);
        if (process.IsClosed || process.IsInvalid) throw new ArgumentException("A retained process handle is required", nameof(process));
        if (!JobNative.IsProcessInJob(process, _job, out var member)) throw NativeFailure();
        return member;
    }

    internal bool ContainsTargetHandle(SafeKernelHandle process)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (process.IsInvalid || process.IsClosed) throw new IOException("execution_target_handle_closed");
        if (!JobNative.IsProcessInJob(process, _job, out var member)) throw NativeFailure();
        return member;
    }

    public IReadOnlyList<int> ActiveProcessIds()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        return JobStateReader.ActiveProcessIds(_job);
    }

    public WorkerJobAccounting GetAccounting()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        return JobStateReader.Accounting(_job);
    }

    internal WorkerJobAccounting RequireStoppedFamily()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_observer is null || !_observer.Drain(TimeSpan.FromSeconds(5)) || _observer.Failures.Length != 0)
            throw new IOException("worker_stopped_family_observation_incomplete");
        return StoppedJobFamily.Require(_job, RetainedProcessHandles());
    }

    public Task<bool> StopAsync(TimeSpan timeout) => _targetLifetime.BeginStop(() => StopOwnedAsync(timeout));

    private async Task<bool> StopOwnedAsync(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var elapsed = Stopwatch.StartNew();
        LastStopObservation = null;
        uint creationCount;
        JobProcessDrain captured;
        try
        {
            creationCount = GetAccounting().TotalProcesses;
            var activeIds = ActiveProcessIds();
            _stopObserver?.InventoryCaptured(activeIds);
            captured = new JobProcessDrain(_job, activeIds, RetainedProcessHandles());
        }
        catch (Exception error) when (error is IOException or Win32Exception)
        {
            if (!JobNative.TerminateJobObject(_job, 1)) throw new AggregateException(error, NativeFailure());
            throw;
        }
        using var drain = captured;
        if (!JobNative.TerminateJobObject(_job, 1))
            throw NativeFailure();
        while (elapsed.Elapsed < timeout)
        {
            var accounting = GetAccounting();
            if (accounting.TotalProcesses != creationCount || !drain.IsComplete)
            {
                LastStopObservation = new(accounting.TotalProcesses != creationCount ? "membership_changed" : "inventory_incomplete",
                    creationCount, accounting.TotalProcesses, accounting.ActiveProcesses, drain.IsComplete, drain.AllSignaled);
                return false;
            }
            if (accounting.ActiveProcesses == 0 && ActiveProcessIds().Count == 0 && drain.AllSignaled)
                return true;
            await Task.Delay(20).ConfigureAwait(false);
        }
        var final = GetAccounting();
        LastStopObservation = new("deadline", creationCount, final.TotalProcesses, final.ActiveProcesses, drain.IsComplete, drain.AllSignaled);
        return false;
    }

    public void Dispose()
    {
        if (!_targetLifetime.BeginDispose(() => { lock (_handleGate) _isDisposed = true; })) return;
        try { _observer?.Dispose(); }
        finally
        {
            _job.Dispose();
            lock (_handleGate)
            {
                foreach (var process in _processHandles.Concat(_observedHandles.Values)) process.Dispose();
                _processHandles.Clear(); _observedHandles.Clear(); _observedIdentities.Clear();
            }
            _roots.Clear();
        }
    }

    internal OwnedTargetInventory CaptureTargetInventory()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_observer is null || !_observer.Drain(TimeSpan.FromSeconds(5)) || _observer.Failures.Length != 0)
            throw new IOException("execution_target_inventory_incomplete");
        var before = GetAccounting();
        var active = ActiveProcessIds().Order().ToArray();
        var handles = RetainedProcessHandles().GroupBy(process =>
        {
            if (!JobNative.GetProcessTimes(process, out var created, out _, out _, out _)) throw NativeFailure();
            var pid = checked((int)JobNative.GetProcessId(process));
            if (pid < 1) throw NativeFailure();
            return (Pid: pid, Created: created);
        }).Select(group => (group.Key.Pid, Handle: group.First())).ToArray();
        var all = System.Collections.Immutable.ImmutableArray.CreateBuilder<ProcessIdentity>();
        var living = System.Collections.Immutable.ImmutableArray.CreateBuilder<ProcessIdentity>();
        foreach (var item in handles)
        {
            var identity = ObserveIdentity(item.Handle, item.Pid); all.Add(identity);
            var wait = JobNative.WaitForSingleObject(item.Handle, 0);
            if (wait == 0) continue;
            if (wait != 258 || !ContainsTargetHandle(item.Handle)) throw new IOException("execution_target_inventory_incomplete");
            living.Add(identity);
        }
        var after = GetAccounting();
        var family = living.OrderBy(item => item.ProcessId).ToImmutableArray();
        if (before.TotalProcesses != after.TotalProcesses || before.ActiveProcesses != after.ActiveProcesses ||
            after.TotalProcesses != handles.Length || after.ActiveProcesses != family.Length ||
            !active.SequenceEqual(family.Select(item => item.ProcessId)) || !active.SequenceEqual(ActiveProcessIds().Order()))
            throw new IOException("execution_target_inventory_changed");
        return new(all.OrderBy(item => item.ProcessId).ThenBy(item => item.CreatedAtUtc).ToImmutableArray(), family,
            after.TotalProcesses, after.ActiveProcesses, handles.Length, _targetLifetime.ReadFlags());
    }

    internal bool HasTargetLifetimeChange => _targetLifetime.HasChange;

    internal OwnedTargetHandoff HandoffTarget(NexusPipeline.Platform.Storage.Execution.PreparedRetentionIntent intent) =>
        _targetLifetime.Handoff(intent);

    internal T UseTargetHandoff<T>(OwnedTargetHandoff handoff, Func<T> callback) =>
        _targetLifetime.UseHandoff(handoff, callback);

    private void ReserveObservedIdentity(SafeKernelHandle process, int pid)
    {
        if (_reserveObservedIdentity is not { } reserve) return;
        if (!JobNative.GetProcessTimes(process, out var created, out _, out _, out _)) throw NativeFailure();
        reserve(pid, created);
    }

    private ProcessIdentity ObserveIdentity(SafeKernelHandle process, int pid, ProcessIdentityLease? imageProtection = null)
    {
        if (!JobNative.GetProcessTimes(process, out var created, out _, out _, out _)) throw NativeFailure();
        var pinned = imageProtection?.Read(process, pid);
        lock (_handleGate)
            if (_observedIdentities.TryGetValue((pid, created), out var cached) && (pinned is null || pinned == cached)) return cached;
        _reserveObservedIdentity?.Invoke(pid, created);
        if (JobNative.WaitForSingleObject(process, 0) != 258) throw new IOException("worker_process_identity_not_observed");
        var identity = pinned ?? ProcessIdentityReader.Read(process, pid);
        if (identity.ProcessId != pid || identity.CreatedAtUtc.ToFileTimeUtc() != created)
            throw new IOException("worker_process_identity_changed");
        lock (_handleGate)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(OwnedWorkerJob));
            if (_observedIdentities.TryGetValue((pid, created), out var existing))
            {
                if (existing != identity) throw new IOException("worker_process_identity_changed");
                return existing;
            }
            if (_observedIdentities.Count >= 4096) throw new IOException("worker_observed_family_limit");
            _observedIdentities.Add((pid, created), identity);
        }
        return identity;
    }

    private OwnedWorkerIdentity ReadIdentity(SafeKernelHandle process, int pid, Guid instanceId, Guid attemptId, ProcessIdentityLease? imageProtection)
    {
        var identity = ObserveIdentity(process, pid, imageProtection);
        return new(identity.ProcessId, identity.CreatedAtUtc, identity.ImagePath, identity.ImageSha256,
            identity.UserSid, identity.IsElevated, instanceId, attemptId);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static Win32Exception NativeFailure() => new(Marshal.GetLastPInvokeError());
}
