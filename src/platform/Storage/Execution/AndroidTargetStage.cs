using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class AndroidTargetStage : IDisposable
{
    private readonly AndroidControlStore _store;
    private readonly ExecutionPlanStep _step;
    private readonly BindingRawAllowance _raw;
    private readonly AndroidObservationAllowance _identities;
    private readonly List<AndroidControlProcess> _commands = [];
    private readonly List<(AndroidCommandRegistration Request, ExecutionAndroidObservation Meaning)> _startup = [];
    private readonly TimeSpan _stopTimeout, _drainTimeout;
    private readonly Func<TimeSpan>? _bindingRemaining;
    private readonly Stopwatch _readiness = new();
    private BlobReference? _previous;
    private TargetExecutionOutcome? _outcome;
    private TargetCleanupFact? _cleanup;
    private bool _cleanupStarted;
    private bool _attempted, _startupPublished, _finished, _disposed;

    internal AndroidTargetStage(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        ResourceLeaseManager resources, AcceptedExecutionAdmission accepted, ExecutionPlanStep step, BindingRawAllowance raw, ITargetPersistenceObserver? observer = null, Func<TimeSpan>? bindingRemaining = null)
    {
        var total = accepted.Prepared.Total.Plan.ReadWire();
        _step = total.Steps.Single(item => item.StepId == step.StepId);
        if (_step.Target is not { Kind: "android", Android: not null, Pc: null }) throw new IOException("execution_android_target_required");
        _bindingRemaining = bindingRemaining; _raw = raw; _identities = new(AndroidControlCapacity.Derive(_step));
        _stopTimeout = TimeSpan.FromSeconds(Math.Min(total.Budget.StopTimeoutSeconds, 60));
        _drainTimeout = TimeSpan.FromSeconds(Math.Min(total.Budget.DrainTimeoutSeconds, 60));
        _store = AndroidControlStore.ForAccepted(instance, database, contracts, lease, resources, accepted, step.StepId, observer);
    }

    internal TargetExecutionOutcome? Outcome => _outcome;
    internal bool CanRunMain => _startupPublished && _outcome is TargetExecutionOutcome.Skipped or TargetExecutionOutcome.ControlledReady;
    internal TimeSpan StopTimeout => _stopTimeout;

    internal PreparedAndroidDevice PrepareDevice(StepEvidenceBinding primary)
    {
        RequireLive();
        if (!CanRunMain || _cleanupStarted || _finished || _store.AcceptedStop() is not null)
            throw new IOException("execution_device_target_unbound");
        return _store.PrepareDevice(primary);
    }

    internal async Task<ExecutionStopReason?> RunAsync(Func<ExecutionStopReason?> readStop, CancellationToken hostStopping)
    {
        RequireLive();
        if (_attempted) throw new IOException("execution_android_stage_already_attempted");
        _attempted = true;
        if (Stop(readStop, hostStopping) is { } initial) return Defer(initial);
        if (!_step.Target!.LaunchByHost) { Record(TargetExecutionOutcome.Skipped); return null; }
        while (true)
        {
            if (Stop(readStop, hostStopping) is { } requested) return Defer(requested);
            var next = AndroidControlSequence.Next(_step.Target.Android!.Driver, _step.Target.Android.Package, _startup, _step.Target.Android.Emulator is not null);
            if (next is null) { Record(TargetExecutionOutcome.ControlledReady); return null; }
            var limit = _store.Remaining(cleanup: false);
            var bindingLeft = _bindingRemaining?.Invoke() ?? TimeSpan.MaxValue;
            if (bindingLeft <= TimeSpan.Zero) { Record(TargetExecutionOutcome.ReadinessTimedOut); return null; }
            if (bindingLeft < limit) limit = bindingLeft;
            if (_readiness.IsRunning)
            {
                var remaining = TimeSpan.FromSeconds(_step.Target.WaitSeconds) - _readiness.Elapsed;
                var binding = _bindingRemaining?.Invoke() ?? TimeSpan.MaxValue;
                if (binding < remaining) remaining = binding;
                if (remaining <= TimeSpan.Zero) { Record(TargetExecutionOutcome.ReadinessTimedOut); return null; }
                if (remaining < limit) limit = remaining;
            }
            var observed = await IssueAsync(next.Value.Purpose, next.Value.Action, limit, () => Stop(readStop, hostStopping)).ConfigureAwait(false);
            if (Stop(readStop, hostStopping) is { } after) return Defer(after);
            _startup.Add((_store.Registration(observed.Request), observed.Meaning));
            if ((_bindingRemaining?.Invoke() ?? TimeSpan.MaxValue) <= TimeSpan.Zero && observed.Meaning.State != ExecutionAndroidState.Failed &&
                !(observed.Meaning is { State: ExecutionAndroidState.Foreground, Interpretation: TargetCommandObservation.Complete } &&
                    observed.Meaning.ForegroundPackage == _step.Target.Android.Package))
            { Record(TargetExecutionOutcome.ReadinessTimedOut); return null; }
            if (observed.Meaning.State == ExecutionAndroidState.Failed)
            { Record(TargetExecutionOutcome.LaunchFailed); return null; }
            if (observed.Meaning.State == ExecutionAndroidState.Unknown)
            {
                Record(_readiness.IsRunning && _readiness.Elapsed >= TimeSpan.FromSeconds(_step.Target.WaitSeconds)
                    ? TargetExecutionOutcome.ReadinessTimedOut : TargetExecutionOutcome.ReadinessUnknown);
                return null;
            }
            if (observed.Meaning is { State: ExecutionAndroidState.Foreground, Interpretation: TargetCommandObservation.Complete } &&
                observed.Meaning.ForegroundPackage == _step.Target.Android.Package)
            { Record(TargetExecutionOutcome.ControlledReady); return null; }
            if (next.Value.Action == ExecutionAndroidAction.StartApplication) _readiness.Start();
            if (next.Value.Purpose == AndroidCommandPurpose.AwaitReadiness)
            {
                var remaining = TimeSpan.FromSeconds(_step.Target.WaitSeconds) - _readiness.Elapsed;
                var binding = _bindingRemaining?.Invoke() ?? TimeSpan.MaxValue;
                if (binding < remaining) remaining = binding;
                if (remaining <= TimeSpan.Zero) { Record(TargetExecutionOutcome.ReadinessTimedOut); return null; }
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(AndroidControlCapacity.PollMilliseconds)
                    ? remaining : TimeSpan.FromMilliseconds(AndroidControlCapacity.PollMilliseconds)).ConfigureAwait(false);
            }
        }
    }

    internal PreparedCaptureContext PrepareCapture(StepEvidenceBinding primary)
    {
        RequireLive();
        if (!CanRunMain || _cleanupStarted || _finished || _store.AcceptedStop() is not null)
            throw new IOException("execution_capture_android_unbound");
        return _store.PrepareCapture(primary);
    }

    internal void StartMain(Action start)
    {
        RequireLive();
        if (!CanRunMain || _store.AcceptedStop() is not null) throw new IOException("execution_android_main_start_closed");
        start();
    }

    internal async Task FinishAsync()
    {
        RequireLive();
        if (_finished) return;
        foreach (var command in _commands)
        {
            if (command.WasReleasedUnregistered) continue;
            if (!command.TryResolveRegistration())
            {
                await command.ReleaseUnregisteredAsync(_stopTimeout, _drainTimeout).ConfigureAwait(false); continue;
            }
            if (command.IsEmulatorLaunch) continue;
            var proof = await command.CollectAsync(_stopTimeout, _drainTimeout).ConfigureAwait(false);
            _store.RecordCollected(proof);
        }
        if (!_startupPublished && _outcome is not null && _outcome != TargetExecutionOutcome.StoppedByRequest)
        {
            _store.PublishStartup(_outcome.Value, AndroidStartupCodec.Reason(_outcome.Value)); _startupPublished = true;
        }
        if (!_startupPublished)
        {
            var reason = _store.AcceptedStop() ?? throw new IOException("execution_android_stop_not_accepted");
            _outcome = TargetExecutionOutcome.StoppedByRequest;
            _store.PublishStartup(_outcome.Value, CloseReason(reason)); _startupPublished = true;
        }
        if (_cleanup is not null) { await PublishFinalAsync(_cleanup).ConfigureAwait(false); return; }
        if (_cleanupStarted) throw new IOException("execution_android_cleanup_reconciliation_required");
        var decision = _store.CleanupDecision();
        if (_store.HasUnresolvedEffects())
        {
            var unresolved = decision.Application == TargetCleanupAction.None && decision.Device == TargetCleanupAction.None
                ? new TargetCleanupFact(TargetCleanupOutcome.NotRequired, "execution_android_cleanup_not_required")
                : new(TargetCleanupOutcome.Unverified, "execution_android_remote_effect_unverified");
            _cleanup = unresolved; await PublishFinalAsync(unresolved).ConfigureAwait(false); return;
        }
        if (_step.Target!.Android!.Driver == "mumu" &&
            (decision.Application != TargetCleanupAction.None || decision.Device != TargetCleanupAction.None))
        {
            if (_store.Remaining(cleanup: true) <= TimeSpan.Zero)
            {
                _cleanup = new(TargetCleanupOutcome.Unverified, "execution_android_cleanup_deadline");
                await PublishFinalAsync(_cleanup).ConfigureAwait(false); return;
            }
            _cleanupStarted = true;
            _ = await CleanupCommand(AndroidCommandPurpose.CleanupReadiness, ExecutionAndroidAction.DeviceState).ConfigureAwait(false);
            if (!_store.HasCleanupMapping())
            {
                _cleanup = new(TargetCleanupOutcome.Unverified, "execution_mumu_current_mapping_unproven");
                await PublishFinalAsync(_cleanup).ConfigureAwait(false); return;
            }
        }
        var cleanup = new TargetCleanupFact(TargetCleanupOutcome.NotRequired, "execution_android_cleanup_not_required");
        if ((decision.Application == TargetCleanupAction.StopAndroidApplication || decision.Device == TargetCleanupAction.ShutdownAndroid) &&
            _store.Remaining(cleanup: true) <= TimeSpan.Zero)
        {
            cleanup = new(TargetCleanupOutcome.Unverified, "execution_android_cleanup_deadline");
            _cleanup = cleanup; await PublishFinalAsync(cleanup).ConfigureAwait(false); return;
        }
        if (decision.Application == TargetCleanupAction.StopAndroidApplication)
        {
            _cleanupStarted = true;
            var app = await CleanupCommand(AndroidCommandPurpose.CleanupApplication, ExecutionAndroidAction.StopApplication).ConfigureAwait(false);
            cleanup = CleanupResult(app.Meaning, TargetCleanupVerification.Application);
        }
        if (decision.Device == TargetCleanupAction.ShutdownAndroid &&
            cleanup.Outcome is TargetCleanupOutcome.NotRequired or TargetCleanupOutcome.Completed)
        {
            if (_store.Remaining(cleanup: true) <= TimeSpan.Zero)
            {
                cleanup = new(TargetCleanupOutcome.Unverified, "execution_android_cleanup_deadline");
                _cleanup = cleanup; await PublishFinalAsync(cleanup).ConfigureAwait(false); return;
            }
            _cleanupStarted = true;
            var shutdown = await CleanupCommand(AndroidCommandPurpose.ShutdownDevice, ExecutionAndroidAction.Shutdown).ConfigureAwait(false);
            cleanup = CleanupResult(shutdown.Meaning, TargetCleanupVerification.None);
            if (cleanup.Outcome == TargetCleanupOutcome.Completed)
                cleanup = await ConfirmOfflineAsync().ConfigureAwait(false);
        }
        _cleanup = cleanup; await PublishFinalAsync(cleanup).ConfigureAwait(false);
    }

    private async Task PublishFinalAsync(TargetCleanupFact cleanup)
    {
        var retain = _store.CleanupDecision().Device == TargetCleanupAction.None;
        foreach (var command in _commands.Where(value => !value.WasReleasedUnregistered && value.IsEmulatorLaunch))
        {
            var collected = retain ? await command.FinishLaunchAsync(_stopTimeout, _drainTimeout).ConfigureAwait(false) :
                await command.CollectAsync(_stopTimeout, _drainTimeout).ConfigureAwait(false);
            _store.RecordCollected(collected);
        }
        _store.PublishCollected(cleanup);
        if (_store.HasUnresolvedEffects()) throw new IOException("execution_android_remote_effect_unverified");
        _finished = true;
    }

    private async Task<TargetCleanupFact> ConfirmOfflineAsync()
    {
        var confirmations = new List<Guid>();
        while (_store.Remaining(cleanup: true) > TimeSpan.Zero)
        {
            var result = await CleanupCommand(AndroidCommandPurpose.ConfirmOffline, ExecutionAndroidAction.OfflineProbe).ConfigureAwait(false);
            if (result.Meaning.State == ExecutionAndroidState.Offline &&
                result.Meaning.Interpretation == TargetCommandObservation.ExpectedOffline)
                confirmations.Add(result.Request.Binding.TargetId);
            else
            {
                confirmations.Clear();
                if (result.Meaning.State != ExecutionAndroidState.Online)
                    return CleanupResult(result.Meaning, TargetCleanupVerification.None);
            }
            if (confirmations.Count == 2) return new(TargetCleanupOutcome.Completed, "execution_android_shutdown_verified")
            { Verification = TargetCleanupVerification.DeviceOffline, ConfirmationCommands = [.. confirmations] };
            var left = _store.Remaining(cleanup: true);
            if (left > TimeSpan.Zero)
                await Task.Delay(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        return new(TargetCleanupOutcome.Unverified, "execution_android_shutdown_unverified");
    }

    private Task<(AndroidCommandRequest Request, ExecutionAndroidObservation Meaning)> CleanupCommand(
        AndroidCommandPurpose purpose, ExecutionAndroidAction action) =>
        IssueAsync(purpose, action, _store.Remaining(cleanup: true), () => null);

    private async Task<(AndroidCommandRequest Request, ExecutionAndroidObservation Meaning)> IssueAsync(
        AndroidCommandPurpose purpose, ExecutionAndroidAction action, TimeSpan timeout, Func<ExecutionStopReason?> readStop)
    {
        if (timeout <= TimeSpan.Zero) throw new IOException("execution_android_command_deadline");
        if (timeout > TimeSpan.FromDays(7)) timeout = TimeSpan.FromDays(7);
        var command = new AndroidControlProcess(_store, _raw, _identities);
        _commands.Add(command);
        try { command.Register(_commands.Count - 1, purpose, action, _previous); }
        catch
        {
            if (!command.TryResolveRegistration()) await command.ReleaseUnregisteredAsync(_stopTimeout, _drainTimeout).ConfigureAwait(false);
            throw;
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(timeout);
        var observed = StartObserveAsync();
        async Task<PreparedAndroidObservation?> StartObserveAsync()
        {
            var elapsed = Stopwatch.StartNew();
            await command.StartAsync(cancellation.Token).ConfigureAwait(false);
            var remaining = timeout - elapsed.Elapsed;
            return remaining <= TimeSpan.Zero ? null : await command.ObserveAsync(remaining, cancellation.Token).ConfigureAwait(false);
        }
        var requested = false;
        while (!observed.IsCompleted)
        {
            if (readStop() is not null)
            {
                requested = true; cancellation.Cancel();
                await command.StopAsync(_stopTimeout).ConfigureAwait(false); break;
            }
            await Task.WhenAny(observed, Task.Delay(TimeSpan.FromMilliseconds(50))).ConfigureAwait(false);
        }
        PreparedAndroidObservation? proof;
        try { proof = await observed.ConfigureAwait(false); }
        catch (OperationCanceledException) when (requested || cancellation.IsCancellationRequested) { proof = null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { proof = null; }
        if (proof is null)
        {
            var collected = await command.CollectAsync(_stopTimeout, _drainTimeout).ConfigureAwait(false);
            _store.RecordCollected(collected); proof = collected.Observation;
        }
        _previous = _store.RecordObserved(proof);
        if (AndroidControlAdmission.IsCleanup(purpose))
            _store.RecordCollected(await command.CollectAsync(_stopTimeout, _drainTimeout).ConfigureAwait(false));
        return (command.Request, _store.Meaning(command.Request, _previous));
    }

    private void Record(TargetExecutionOutcome outcome)
    {
        _outcome = outcome;
        _store.PublishStartup(outcome, AndroidStartupCodec.Reason(outcome)); _startupPublished = true;
    }

    private ExecutionStopReason Defer(ExecutionStopReason reason) { _outcome = TargetExecutionOutcome.StoppedByRequest; return reason; }

    private ExecutionStopReason? Stop(Func<ExecutionStopReason?> external, CancellationToken hostStopping) =>
        _store.AcceptedStop() ?? external() ?? (hostStopping.IsCancellationRequested ? ExecutionStopReason.Interrupted :
            _store.Remaining(cleanup: false) == TimeSpan.Zero ? ExecutionStopReason.TotalBudgetExhausted : null);

    private static TargetCleanupFact CleanupResult(ExecutionAndroidObservation observation, TargetCleanupVerification verification) =>
        observation.State == ExecutionAndroidState.Failed
            ? new(TargetCleanupOutcome.Failed, "execution_android_cleanup_failed")
            : observation.State == ExecutionAndroidState.Unknown || observation.Interpretation is TargetCommandObservation.Unverified or
                TargetCommandObservation.Incomplete or TargetCommandObservation.Contradictory
                ? new(TargetCleanupOutcome.Unverified, "execution_android_cleanup_unverified")
                : new(TargetCleanupOutcome.Completed, "execution_android_cleanup_verified") { Verification = verification };

    private static string CloseReason(ExecutionStopReason reason) => reason switch
    {
        ExecutionStopReason.Cancelled => DispatchIntentCodec.Reason,
        ExecutionStopReason.TotalBudgetExhausted => DispatchIntentCodec.BudgetReason,
        ExecutionStopReason.Interrupted => DispatchIntentCodec.InterruptionReason,
        ExecutionStopReason.TargetUnavailable => DispatchIntentCodec.TargetReason,
        _ => throw new IOException("execution_android_stop_unrecognized")
    };

    private void RequireLive() { ObjectDisposedException.ThrowIf(_disposed, this); _store.RequireLive(); }

    public void Dispose()
    {
        if (_disposed) return;
        if (!_finished) throw new IOException("execution_android_stage_collection_pending");
        foreach (var command in _commands) command.Dispose();
        _store.Dispose(); _disposed = true;
    }
}
