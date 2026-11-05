using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Processes;

internal sealed record MaintenanceStartupInventory(ImmutableArray<ProcessIdentity> AllMembers,
    ImmutableArray<ProcessIdentity> LivingMembers, uint TotalProcesses, uint ActiveProcesses);

[SupportedOSPlatform("windows")]
internal sealed class MaintenanceStartupJob : IDisposable
{
    private readonly object _gate = new();
    private readonly SafeKernelHandle _job;
    private readonly MaintenanceStartupOrigin _origin;
    private readonly ProcessStartObserver _observer;
    private readonly Dictionary<(int, long), (SafeKernelHandle Handle, ProcessIdentity Identity)> _members = [];
    private readonly Action<ProcessIdentity> _recordMember;
    private MaintenanceStartupProcess? _root;
    private OwnedWorkerIdentity? _rootIdentity, _registering;
    private bool _attempted, _disposed;
    private Task<bool>? _stop;
    internal string Name { get; } = "Local\\NexusWorker-" + Guid.NewGuid().ToString("N");

    internal MaintenanceStartupJob(MaintenanceStartupOrigin origin, Action<ProcessIdentity> recordMember)
    {
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _origin.RequireCurrent();
        _recordMember = recordMember ?? throw new ArgumentNullException(nameof(recordMember));
        _job = JobNative.CreateJobObjectW(IntPtr.Zero, Name);
        if (_job.IsInvalid) throw Failure();
        try
        {
            if (Marshal.GetLastPInvokeError() == 183) throw Invalid();
            var limits = new JobNative.ExtendedLimits { Basic = new() { Flags = 0x2800 } };
            if (!JobNative.SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<JobNative.ExtendedLimits>())) throw Failure();
            _observer = new(_job, RetainMember);
        }
        catch { _job.Dispose(); throw; }
    }

    internal OwnedWorkerIdentity Start(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        Guid instanceId, Guid attemptId, Action<OwnedWorkerIdentity> persistBeforeExecution, OwnedProcessOutput? output = null,
        MaintenanceStartupRole role = MaintenanceStartupRole.HelperBootstrap)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(persistBeforeExecution);
            if (_attempted || _stop is not null || instanceId == Guid.Empty || attemptId == Guid.Empty ||
                GetAccounting() is not { TotalProcesses: 0, ActiveProcesses: 0 } ||
                ActiveProcessIds().Count != 0 || JobStateReader.Limits(_job).Basic.Flags != 0x2800) throw Invalid();
            _origin.RequireCurrent();
            _attempted = true;
            _root = MaintenanceStartupProcess.Create(_job, executable, arguments, workingDirectory, output, role);
            try
            {
                RequireSuspendedRoot();
                var identity = _root.ReadFrozenIdentity(instanceId, attemptId);
                _registering = identity;
                try { persistBeforeExecution(identity); }
                finally { _registering = null; }
                RequireSuspendedRoot();
                _rootIdentity = identity;
                _root.Resume();
                return identity;
            }
            catch (Exception failure)
            {
                if (_root.StopSuspended() is { } stopFailure)
                    throw new AggregateException("maintenance_startup_stop_pending", failure, stopFailure);
                throw;
            }
        }
    }

    internal void RequirePersistingRoot(OwnedWorkerIdentity actual)
    {
        lock (_gate)
        {
            if (_registering != actual || _root is null || _root.ReadFrozenIdentity(actual.InstanceId, actual.AttemptId) != actual)
                throw Invalid();
            RequireSuspendedRoot();
        }
    }

    private void RequireSuspendedRoot()
    {
        _origin.RequireCurrent();
        var before = GetAccounting();
        if (_root is null || !JobNative.IsProcessInJob(_root.Process, _job, out var member) || !member ||
            JobNative.WaitForSingleObject(_root.Process, 0) != 258 || before is not { TotalProcesses: 1, ActiveProcesses: 1 } ||
            !ActiveProcessIds().SequenceEqual([_root.ProcessId]) || JobStateReader.Limits(_job).Basic.Flags != 0x2800) throw Invalid();
        var after = GetAccounting();
        if (after.TotalProcesses != before.TotalProcesses || after.ActiveProcesses != before.ActiveProcesses) throw Invalid();
        _origin.RequireChildExcluded(_root.Process);
    }

    internal MaintenanceJobQuery RetainSuccessorJob(ProcessIdentity expectedRoot)
    {
        _origin.RequireCurrent();
        var family = CapturePreparedFamily();
        if (family is not { TotalProcesses: 1, ActiveProcesses: 1 } ||
            !family.LivingMembers.SequenceEqual([expectedRoot])) throw Invalid();
        lock (_gate)
        {
            if (_root is null || _rootIdentity is null || _rootIdentity.ProcessId != expectedRoot.ProcessId ||
                _rootIdentity.CreatedAtUtc != expectedRoot.CreatedAtUtc) throw Invalid();
            _origin.RequireChildExcluded(_root.Process);
            return MaintenanceJobQuery.Retain(_job);
        }
    }

    internal void ReleasePreparedRoot(ProcessIdentity expectedRoot)
    {
        using var retained = RetainSuccessorJob(expectedRoot);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stop is not null || JobStateReader.Limits(_job).Basic.Flags != 0x2800) throw Invalid();
            var limits = new JobNative.ExtendedLimits { Basic = new() { Flags = 0x800 } };
            // The prepared Host outlives this helper; closing its final query handle must not terminate retained games.
            if (!JobNative.SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<JobNative.ExtendedLimits>())) throw Failure();
            if (retained.Flags != 0x800) throw Invalid();
        }
    }

    private void RetainMember(int processId, SafeKernelHandle process)
    {
        var identity = ProcessIdentityReader.Read(process, processId);
        if (!JobNative.DuplicateHandle(new IntPtr(-1), process, new IntPtr(-1), out var retained, 0, false, 2)) throw Failure();
        lock (_gate)
        {
            var key = (identity.ProcessId, identity.CreatedAtUtc.ToFileTimeUtc());
            if (_disposed || _members.ContainsKey(key)) { retained.Dispose(); return; }
            if (_members.Count >= 128) { retained.Dispose(); throw new IOException("maintenance_bootstrap_family_limit"); }
            _members.Add(key, (retained, identity));
        }
        _recordMember(identity);
    }

    internal WorkerJobAccounting GetAccounting() => JobStateReader.Accounting(_job);
    internal IReadOnlyList<int> ActiveProcessIds() => JobStateReader.ActiveProcessIds(_job);

    internal MaintenanceStartupInventory CapturePreparedFamily()
    {
        if (!_observer.Drain(TimeSpan.FromSeconds(5)) || _observer.Failures.Length != 0) throw Invalid();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_root is null || _rootIdentity is null || _stop is not null ||
                JobStateReader.Limits(_job).Basic.Flags != 0x2800) throw Invalid();
            var before = GetAccounting();
            var living = ImmutableArray.CreateBuilder<ProcessIdentity>();
            foreach (var member in _members.Values)
            {
                var wait = JobNative.WaitForSingleObject(member.Handle, 0);
                if (wait == 0) continue;
                if (wait != 258 || !JobNative.IsProcessInJob(member.Handle, _job, out var belongs) || !belongs) throw Invalid();
                living.Add(member.Identity);
            }
            var active = living.OrderBy(item => item.ProcessId).ToImmutableArray();
            var after = GetAccounting();
            if (before.TotalProcesses != after.TotalProcesses || before.ActiveProcesses != after.ActiveProcesses ||
                after.TotalProcesses != _members.Count || after.ActiveProcesses != active.Length ||
                !active.Any(item => item.ProcessId == _rootIdentity.ProcessId && item.CreatedAtUtc == _rootIdentity.CreatedAtUtc) ||
                !ActiveProcessIds().Order().SequenceEqual(active.Select(item => item.ProcessId))) throw Invalid();
            return new(_members.Values.Select(item => item.Identity).OrderBy(item => item.ProcessId)
                .ThenBy(item => item.CreatedAtUtc).ToImmutableArray(), active, after.TotalProcesses, after.ActiveProcesses);
        }
    }

    internal async Task<int?> AwaitRootAsync(OwnedWorkerIdentity identity, TimeSpan timeout)
    {
        SafeKernelHandle process;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_root is null || identity != _rootIdentity || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw Invalid();
            process = _root.Process;
        }
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            var wait = JobNative.WaitForSingleObject(process, 0);
            if (wait == 0)
            {
                if (!JobNative.GetExitCodeProcess(process, out var code)) throw Failure();
                return unchecked((int)code);
            }
            if (wait != 258) throw Failure();
            await Task.Delay(20).ConfigureAwait(false);
        }
        return null;
    }

    internal Task<bool> StopAsync(TimeSpan timeout)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
            return _stop ??= StopCoreAsync(timeout);
        }
    }

    internal ProcessIdentity RequireStoppedRoot(OwnedWorkerIdentity expectedRoot)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_root is null || _rootIdentity != expectedRoot || _stop is not { IsCompletedSuccessfully: true } || !_stop.Result ||
                GetAccounting() is not { TotalProcesses: 1, ActiveProcesses: 0 } || ActiveProcessIds().Count != 0 ||
                JobNative.WaitForSingleObject(_root.Process, 0) != 0) throw Invalid();
            // A stopped candidate's image lease must close before its payload can be retired for rollback.
            _root.ReleaseStoppedImage();
            return new(expectedRoot.ProcessId, expectedRoot.CreatedAtUtc, expectedRoot.ImagePath,
                expectedRoot.ImageSha256, expectedRoot.UserSid, expectedRoot.IsElevated);
        }
    }

    private async Task<bool> StopCoreAsync(TimeSpan timeout)
    {
        SafeKernelHandle[] retained;
        lock (_gate) retained = _members.Values.Select(item => item.Handle).Concat(_root is null ? [] : new[] { _root.Process }).ToArray();
        var count = GetAccounting().TotalProcesses;
        using var drain = new JobProcessDrain(_job, ActiveProcessIds(), retained);
        if (!JobNative.TerminateJobObject(_job, 1)) throw Failure();
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            var accounting = GetAccounting();
            if (accounting.TotalProcesses != count || !drain.IsComplete) return false;
            if (accounting.ActiveProcesses == 0 && ActiveProcessIds().Count == 0 && drain.AllSignaled) return true;
            await Task.Delay(20).ConfigureAwait(false);
        }
        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_stop is { IsCompleted: false }) throw new IOException("maintenance_startup_stop_pending");
            _disposed = true;
        }
        try { _observer.Dispose(); }
        finally
        {
            _job.Dispose(); _root?.Dispose();
            lock (_gate) { foreach (var member in _members.Values) member.Handle.Dispose(); _members.Clear(); }
        }
    }

    private static Win32Exception Failure() => new(Marshal.GetLastPInvokeError());
    private static IOException Invalid() => new("maintenance_startup_topology_unconfirmed");
}
