using System.Runtime.Versioning;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class ProviderLauncherProcess : IDisposable
{
    private readonly object _gate = new();
    private readonly OwnedWorkerJob _job = new();
    private readonly ProviderLauncherStore _store;
    private readonly ITargetPersistenceObserver? _observer;
    private ProviderLauncherBinding? _binding;
    private PreparedLauncherStart? _started;
    private PreparedStoppedTarget? _stopped;
    private PreparedRetentionIntent? _intent;
    private OwnedTargetHandoff? _handoff;
    private Task? _stopping;
    private bool _startAttempted, _closing, _resolved, _transferred, _disposed;

    internal ProviderLauncherProcess(ProviderLauncherStore store, ITargetPersistenceObserver? observer = null)
    {
        try { _store = store; _observer = observer; _job.ObserveProcessIdentities(_ => { }); }
        catch { _job.Dispose(); store.Dispose(); throw; }
    }

    internal void Register()
    {
        lock (_gate)
        {
            RequireOpen();
            if (_binding is null) _binding = _store.Register(_job);
        }
    }

    internal void Start()
    {
        lock (_gate)
        {
            RequireOpen();
            if (_startAttempted) throw new IOException("execution_provider_launcher_start_already_attempted");
            _startAttempted = true;
            _started = PreparedLauncherStart.Start(_store, Binding(), _job);
            if (!_started.Succeeded) throw new IOException("execution_provider_launcher_native_start_failed");
            _store.RecordStarted(_started);
        }
    }

    internal async Task FinishAsync(TimeSpan timeout)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_resolved) return;
        }
        if (_started?.Succeeded != true) { await StopAsync(timeout).ConfigureAwait(false); return; }
        lock (_gate)
        {
            if (_resolved) return;
            _closing = true;
            if (!_job.HasTargetLifetimeChange && _job.CaptureTargetInventory().ActiveProcesses == 0)
            { _stopping ??= StopCoreAsync(timeout); }
            else
            {
                try
                {
                    for (var attempt = 0; _handoff is null; attempt++)
                    {
                        _intent ??= _store.PrepareRetention(Binding(), _job, _job.CaptureTargetInventory());
                        try { _handoff = _job.HandoffTarget(_intent); }
                        catch (IOException error) when (!_job.HasTargetLifetimeChange &&
                            error.Message == "execution_target_inventory_changed" && attempt + 1 < ProviderLauncherCodec.MaximumRetentionIntents)
                        { _intent = null; }
                    }
                    _observer?.Reached(TargetPersistencePoint.HandoffApplied, Binding());
                    _ = _store.RecordRetained(Binding(), _job, _intent ?? throw new IOException("execution_provider_retention_intent_missing"), _handoff);
                    _transferred = true; _resolved = true; return;
                }
                catch (Exception error) when (!_job.HasTargetLifetimeChange && (error is IOException or ObjectDisposedException))
                { _stopping ??= StopCoreAsync(timeout); }
            }
        }
        await _stopping!.ConfigureAwait(false);
    }

    internal Task StopAsync(TimeSpan timeout)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
            if (_handoff is not null || _job.HasTargetLifetimeChange) throw new IOException("execution_provider_launcher_lifetime_transferred");
            if (_resolved) return Task.CompletedTask;
            _closing = true;
            if (_stopping is { IsCompleted: false }) return _stopping;
            return _stopping = StopCoreAsync(timeout);
        }
    }

    private async Task StopCoreAsync(TimeSpan timeout)
    {
        var proof = _stopped ?? await PreparedStoppedTarget.StopAsync(_job, timeout).ConfigureAwait(false);
        lock (_gate)
        {
            _stopped = proof; _binding ??= _store.FindRegistered(_job);
            if (_binding is not null) _store.RecordStopped(_binding, _job, proof, _started);
            else proof.Use(_job, inventory =>
            {
                if (inventory.TotalProcesses != 0 || inventory.RetainedKernelObjects != 0)
                    throw new IOException("execution_provider_launcher_unregistered_effects");
                return true;
            });
            _resolved = true;
        }
    }

    private ProviderLauncherBinding Binding() => _binding ?? throw new IOException("execution_provider_launcher_registration_missing");
    private void RequireOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_closing || _resolved) throw new IOException("execution_provider_launcher_admission_closed");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_resolved) throw new IOException("execution_provider_launcher_collection_pending");
            if (!_transferred) _job.Dispose();
            _store.Dispose(); _disposed = true;
        }
    }
}
