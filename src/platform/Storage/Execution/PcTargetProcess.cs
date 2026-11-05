using System.Runtime.Versioning;
using System.Collections.Immutable;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class PcTargetProcess : IDisposable
{
    private readonly object _gate = new();
    private readonly OwnedWorkerJob _job = new();
    private readonly TargetJobStore _store;
    private readonly string _stepId;
    private PreparedGameRole? _authority;
    private PreparedStoppedTarget? _stopped;
    private PreparedRetentionIntent? _retention;
    private OwnedTargetHandoff? _handoff;
    private Task? _stopping;
    private bool _startAttempted, _startReturned, _closing, _resolved, _disposed;
    internal string JobName => _job.Name;

    internal PcTargetProcess(TargetJobStore store, string stepId)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(store);
            if (string.IsNullOrWhiteSpace(stepId)) throw new ArgumentException("execution_target_step_missing");
            _store = store; _stepId = stepId;
            _job.ObserveProcessIdentities(_ => { });
        }
        catch { _job.Dispose(); throw; }
    }

    internal void Register()
    {
        lock (_gate)
        {
            RequireDispatchOpen();
            if (_authority is not null)
            { _authority.Use(_job, _authority.Binding, _ => true); return; }
            _authority = _store.Register(_job, _stepId);
        }
    }

    internal OwnedWorkerIdentity Start()
    {
        lock (_gate)
        {
            RequireDispatchOpen();
            if (_startAttempted) throw new IOException("execution_target_start_already_attempted");
            var authority = Authority(); _startAttempted = true;
            var result = authority.Use(_job, authority.Binding, facts => _job.StartFrozen(facts.Launch.ExecutablePath,
                facts.Launch.Arguments, facts.Launch.WorkingDirectory, facts.Binding.InstanceId, facts.Binding.AttemptId,
                root => _store.RecordRoot(authority, _job, root), null, facts.CommandLineSyntax));
            _startReturned = true; return result;
        }
    }

    internal PcTargetReadiness Observe(CancellationToken cancellation)
    {
        lock (_gate)
        {
            RequireStarted(); var authority = Authority();
            return authority.Use(_job, authority.Binding, facts => PcTargetReadiness.Capture(facts.ReadyImage, cancellation, facts.ReadinessMode));
        }
    }

    internal void AcceptReady(PcTargetReadiness readiness)
    {
        lock (_gate) { RequireStarted(); _store.RecordReady(Authority(), _job, readiness); }
    }

    internal T UseReady<T>(PcTargetReadiness readiness, Func<ImmutableArray<ProcessIdentity>, T> callback)
    {
        lock (_gate)
        {
            RequireStarted(); var authority = Authority();
            return authority.Use(_job, authority.Binding, facts =>
            {
                readiness.RequireExpected(facts.ReadyImage, facts.ReadinessMode);
                return readiness.UseOwned(_job, callback);
            });
        }
    }

    internal Task StopAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (_gate)
        {
            RequireNotDisposed();
            if (_handoff is not null || _job.HasTargetLifetimeChange)
                throw new IOException("execution_target_lifetime_ownership_transferred");
            if (_resolved) return Task.CompletedTask;
            _closing = true;
            if (_stopping is { IsCompleted: false }) return _stopping;
            return _stopping = StopCoreAsync(timeout);
        }
    }

    private async Task StopCoreAsync(TimeSpan timeout)
    {
        var stopped = _stopped ?? await PreparedStoppedTarget.StopAsync(_job, timeout).ConfigureAwait(false);
        lock (_gate)
        {
            _stopped = stopped;
            _authority ??= _store.FindRegistered(_job, _stepId);
            if (_authority is not null) _store.RecordStopped(_authority, _job, stopped);
            else stopped.Use(_job, inventory =>
            {
                if (inventory.TotalProcesses != 0 || inventory.RetainedKernelObjects != 0)
                    throw new IOException("execution_target_unregistered_effects_unknown");
                return true;
            });
            _resolved = true;
        }
    }

    internal Task ResolveLifetimeAsync(TimeSpan timeout)
    {
        lock (_gate)
        {
            RequireNotDisposed();
            if (_resolved) return Task.CompletedTask;
            if (!_job.HasTargetLifetimeChange && _job.CaptureTargetInventory().ActiveProcesses == 0)
                return StopAsync(timeout);
            Retain(); return Task.CompletedTask;
        }
    }

    internal void Retain()
    {
        lock (_gate)
        {
            RequireNotDisposed();
            if (_stopped is not null || _stopping is not null) throw new IOException("execution_target_stop_already_requested");
            if (_resolved) return;
            if (!_startReturned) throw new IOException("execution_target_start_outcome_unconfirmed");
            _closing = true;
            try
            {
                var authority = Authority();
                _retention ??= _store.PrepareRetention(authority, _job, _job.CaptureTargetInventory());
                _handoff ??= _job.HandoffTarget(_retention);
                _store.RecordRetained(authority, _job, _retention, _handoff);
                _resolved = true;
            }
            catch
            {
                if (_handoff is null && !_job.HasTargetLifetimeChange) _retention = null;
                throw;
            }
        }
    }

    private PreparedGameRole Authority() => _authority ?? throw new IOException("execution_target_registration_missing");
    private void RequireNotDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private void RequireDispatchOpen()
    {
        RequireNotDisposed();
        if (_closing || _resolved) throw new IOException("execution_target_dispatch_closed");
    }
    private void RequireStarted()
    {
        RequireDispatchOpen();
        if (!_startReturned) throw new IOException("execution_target_start_outcome_unconfirmed");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_resolved) throw new IOException("execution_target_collection_pending");
            _job.Dispose(); _disposed = true;
        }
    }
}
