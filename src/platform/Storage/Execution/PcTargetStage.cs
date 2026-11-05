using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class PcTargetStage : IDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly AcceptedExecutionAdmission _accepted;
    private readonly TargetJobStore _store;
    private readonly RegisteredJournal _parent;
    private readonly RunAttemptPlan _attempt;
    private readonly ExecutionPlanStep _step;
    private readonly PcStageBinding _binding;
    private readonly PcTargetImage _image;
    private readonly PcReadinessMode _mode;
    private readonly TimeSpan _stopTimeout;
    private readonly Func<bool>? _bindingExpired;
    private readonly object _gate = new();
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private PcTargetProcess? _process;
    private PcTargetReadiness? _readiness;
    private PcTargetReadiness? _captureReadiness;
    private PreparedPcStage? _proof;
    private PcStageDocument? _frozenDocument;
    private readonly HashSet<PreparedPcWindow> _windows = [];
    private bool _captureClosing;
    private TargetExecutionOutcome? _outcome;
    private string? _reason;
    private bool _attempted, _targetStopped, _cleanupFinished, _disposed;

    internal PcTargetStage(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        ResourceLeaseManager resources, AcceptedExecutionAdmission accepted, ExecutionPlanStep step, ITargetPersistenceObserver? observer = null, Func<bool>? bindingExpired = null)
    {
        lease.VerifyHeld(instance); accepted.Prepared.RequireLive(); _bindingExpired = bindingExpired;
        var total = accepted.Prepared.Total.Plan.ReadWire();
        _step = total.Steps.Single(item => item.StepId == step.StepId);
        if (_step.Target is not { Kind: "pc", Pc: not null, Android: null }) throw new IOException("execution_pc_target_required");
        _instance = instance; _database = database; _lease = lease; _accepted = accepted;
        _attempt = accepted.Prepared.Attempt.Plan.ReadWire();
        _parent = new(accepted.Receipt.JournalId, accepted.Identity.OperationId, JournalKind.RunAttempt, JournalPhase.Prepared, 1,
            accepted.Receipt.Plan.RelativePath, accepted.Receipt.Plan.Sha256, total.ResourceKeys);
        _binding = new(instance.InstanceId, accepted.Receipt.JournalId, accepted.Identity.OperationId,
            Guid.Parse(accepted.Prepared.Attempt.Plan.ReadWire().AttemptId), step.StepId, accepted.Prepared.Attempt.Plan.ReadWire().ExecutionPlan.Sha256);
        var pc = _step.Target.Pc;
        _image = new(pc.ReadyImage.Path, pc.ReadyImage.SizeBytes, pc.ReadyImage.Sha256,
            new(pc.ReadyImage.Identity.VolumeSerial, pc.ReadyImage.Identity.FileId));
        _mode = pc.ReadinessMode == "process" ? PcReadinessMode.Process : PcReadinessMode.VisibleWindow;
        _stopTimeout = TimeSpan.FromSeconds(Math.Min(total.Budget.StopTimeoutSeconds, 60));
        _store = TargetJobStore.ForAccepted(instance, database, contracts, lease, resources, accepted, observer);
    }

    internal TargetExecutionOutcome? Outcome => _outcome;
    internal bool CanRunMain => _outcome is TargetExecutionOutcome.Skipped or
        TargetExecutionOutcome.ExternalReady or TargetExecutionOutcome.OwnedReady;

    internal async Task<ExecutionStopReason?> RunAsync(CancellationToken hostStopping)
    {
        lock (_gate)
        {
            Verify();
            if (_attempted) throw new IOException("execution_target_stage_already_attempted");
            _attempted = true;
        }
        if (ReadStop(hostStopping) is { } initial) return DeferStop(initial);
        if (_bindingExpired?.Invoke() == true) { Record(TargetExecutionOutcome.Blocked); return null; }
        if (!_step.Target!.LaunchByHost)
        {
            Record(TargetExecutionOutcome.Skipped); return null;
        }
        var observed = Capture(hostStopping);
        if (ReadStop(hostStopping) is { } afterObservation) return DeferStop(afterObservation);
        if (_bindingExpired?.Invoke() == true) { Record(TargetExecutionOutcome.Blocked); return null; }
        var action = TargetLifecyclePolicy.Launch(new(ExecutionTargetKind.Pc, true, _step.Target.ForceCloseAtEnd),
            observed, ExecutionTargetAuthority.ExternalReadinessOnly, null);
        if (action == TargetLaunchAction.UseReady)
        {
            Record(TargetExecutionOutcome.ExternalReady); return null;
        }
        if (action != TargetLaunchAction.Start)
        {
            Record(TargetExecutionOutcome.Blocked); return null;
        }
        if (ReadStop(hostStopping) is { } beforeStart) return DeferStop(beforeStart);
        _process = new(_store, _step.StepId);
        try { _process.Register(); }
        catch { await StopOwnedAsync().ConfigureAwait(false); throw; }
        try { _process.Start(); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            if (ReadStop(hostStopping) is { } stopped) return DeferStop(stopped);
            await StopOwnedAsync().ConfigureAwait(false);
            Record(TargetExecutionOutcome.LaunchFailed); return null;
        }
        var wait = Stopwatch.StartNew();
        while (true)
        {
            if (ReadStop(hostStopping) is { } requested) return DeferStop(requested);
            if (_bindingExpired?.Invoke() == true)
            {
                await StopOwnedAsync().ConfigureAwait(false); Record(TargetExecutionOutcome.ReadinessTimedOut); return null;
            }
            var state = Capture(hostStopping);
            if (ReadStop(hostStopping) is { } afterPoll) return DeferStop(afterPoll);
            if (state == ExecutionTargetReadiness.Ready)
            {
                try
                {
                    _process.AcceptReady(_readiness!);
                    Record(TargetExecutionOutcome.OwnedReady); return null;
                }
                catch (IOException error)
                {
                    if (ReadStop(hostStopping) is { } stopped) return DeferStop(stopped);
                    if (error.Message is not ("execution_target_not_owned_by_job" or "execution_target_readiness_expired")) throw;
                    await StopOwnedAsync().ConfigureAwait(false);
                    Record(TargetExecutionOutcome.ReadinessUnknown); return null;
                }
            }
            if (state == ExecutionTargetReadiness.Unknown)
            {
                await StopOwnedAsync().ConfigureAwait(false);
                Record(TargetExecutionOutcome.ReadinessUnknown); return null;
            }
            if (wait.Elapsed >= TimeSpan.FromSeconds(_step.Target.WaitSeconds))
            {
                await StopOwnedAsync().ConfigureAwait(false);
                Record(TargetExecutionOutcome.ReadinessTimedOut); return null;
            }
            var remaining = _accepted.Prepared.TimeBudget.Limit(TimeSpan.FromMilliseconds(100));
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining).ConfigureAwait(false);
        }
    }

    internal void StartMain(Action start)
    {
        lock (_gate)
        {
            Verify();
            if (!CanRunMain || _proof is null || ReadStop(CancellationToken.None) is not null)
                throw new IOException("execution_target_main_start_unavailable");
            if (_outcome == TargetExecutionOutcome.Skipped) { start(); return; }
            _readiness!.RequireExpected(_image, _mode);
            if (_outcome == TargetExecutionOutcome.ExternalReady) _readiness.Use(_ => { start(); return true; });
            else _process!.UseReady(_readiness, _ => { start(); return true; });
        }
    }

    internal PreparedPcWindow PrepareWindow()
    {
        lock (_gate)
        {
            VerifyWindowAdmission();
            if (_windows.Count >= 16) throw new IOException("execution_capture_borrow_limit");
            if (_windows.FirstOrDefault() is { } existing)
                return UseWindow(existing, facts => { var borrow = new PreparedPcWindow(this, facts); _windows.Add(borrow); return borrow; });
            if (_outcome == TargetExecutionOutcome.Skipped)
                _captureReadiness = PcTargetReadiness.Capture(_image, CancellationToken.None, _mode);
            try { return UseCurrentReady(members =>
            {
                var state = _store.Read(_binding.ParentJournalId);
                var ready = PcStageCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
                    .Single(item => item.Binding == _binding);
                foreach (var process in members.OrderBy(item => item.ProcessId))
                {
                    if (_frozenDocument is null || _outcome != TargetExecutionOutcome.Skipped &&
                        !_frozenDocument.ReadyMembers.Contains(process))
                        throw new IOException("execution_capture_readiness_changed");
                    var observed = WindowSnapshotProbe.Capture(process.ProcessId, "", CancellationToken.None);
                    if (observed.Truncated) throw new IOException("execution_capture_window_inventory_incomplete");
                    foreach (var window in observed.Windows)
                    {
                        var handle = long.Parse(window.Hwnd, System.Globalization.CultureInfo.InvariantCulture);
                        (int Width, int Height) dimensions;
                        try { dimensions = NexusPipeline.Platform.Media.WindowCaptureWorker.Dimensions(new IntPtr(handle), process.ProcessId); }
                        catch (IOException) { continue; }
                        var borrow = new PreparedPcWindow(this, new(_binding, _outcome!.Value, ready.Artifact,
                            process, handle, dimensions.Width, dimensions.Height));
                        _windows.Add(borrow); return borrow;
                    }
                }
                throw new IOException("execution_capture_window_unavailable");
            }); }
            catch { _captureReadiness?.Dispose(); _captureReadiness = null; throw; }
        }
    }

    internal T UseWindow<T>(PreparedPcWindow actual, Func<PcWindowFacts, T> effect)
    {
        lock (_gate)
        {
            VerifyWindowAdmission();
            if (!_windows.Contains(actual) || actual.Facts.Binding != _binding)
                throw new IOException("execution_capture_window_unbound");
            return UseCurrentReady(members =>
            {
                var facts = actual.Facts;
                if (!members.Contains(facts.Process) || NexusPipeline.Platform.Media.WindowCaptureWorker.Dimensions(new IntPtr(facts.WindowHandle), facts.Process.ProcessId) !=
                    (facts.Width, facts.Height)) throw new IOException("execution_capture_window_changed");
                return effect(facts);
            });
        }
    }

    internal void ReleaseWindow(PreparedPcWindow actual)
    {
        lock (_gate)
        {
            if (!_windows.Remove(actual)) throw new IOException("execution_capture_window_unbound");
            if (_windows.Count == 0) { _captureReadiness?.Dispose(); _captureReadiness = null; }
        }
    }

    internal void ReleaseInput(PreparedPcWindow actual, IReadOnlyList<int> keys, long? pointer, string mouseMethod = "sendMessage", string keyboardMethod = "sendMessage")
    {
        lock (_gate)
        {
            Verify();
            if (_cleanupFinished || !_windows.Contains(actual) || actual.Facts.Binding != _binding)
                throw new IOException("execution_device_release_unbound");
            // Closing admission may release only input already owned by this original borrow.
            UseCurrentReady(members => {
                if (!members.Contains(actual.Facts.Process)) throw new IOException("execution_device_release_unbound");
                PcDeviceInput.Release(actual.Facts, keys, pointer, mouseMethod, keyboardMethod); return true;
            });
        }
    }

    private T UseCurrentReady<T>(Func<ImmutableArray<ProcessIdentity>, T> read)
    {
        var observation = _outcome == TargetExecutionOutcome.Skipped ? _captureReadiness : _readiness;
        if (observation is null) throw new IOException("execution_capture_observation_missing");
        observation.RequireExpected(_image, _mode);
        return _outcome == TargetExecutionOutcome.OwnedReady ? _process!.UseReady(observation, read) : observation.Use(read);
    }

    private void VerifyWindowAdmission()
    {
        Verify();
        var observingOnly = _outcome == TargetExecutionOutcome.Skipped && _step.Target is { LaunchByHost: false };
        if (_captureClosing || _cleanupFinished || _proof is null ||
            !observingOnly && (_readiness is null || _outcome is not
                (TargetExecutionOutcome.OwnedReady or TargetExecutionOutcome.ExternalReady)) ||
            ReadStop(CancellationToken.None) is not null) throw new IOException("execution_capture_admission_closed");
    }

    internal async Task FinishAsync()
    {
        lock (_gate)
        {
            Verify();
            if (_windows.Count != 0) throw new IOException("execution_capture_collection_pending");
            _captureClosing = true;
        }
        if (_cleanupFinished) return;
        if (_proof is null && _attempted)
        {
            if (_process is not null && !_targetStopped && _store.CurrentForStage(_step.StepId)?.Stage == "registered")
                await StopOwnedAsync().ConfigureAwait(false);
            var close = AcceptedClose() ?? throw new IOException("execution_target_stop_not_accepted");
            _reason = close.ReasonCode;
            Record(TargetExecutionOutcome.StoppedByRequest, _reason);
        }
        if (_proof is not null) _store.RecordStage(_proof);
        if (_process is not null && !_targetStopped)
        {
            var decision = _store.ReadCleanup(_step.StepId);
            if (decision.Application == TargetCleanupAction.CloseOwnedPc) await StopOwnedAsync().ConfigureAwait(false);
            else if (decision.Application == TargetCleanupAction.RetainOwned)
                await _process.ResolveLifetimeAsync(_stopTimeout).ConfigureAwait(false);
            else throw new IOException("execution_target_cleanup_unbound");
        }
        _readiness?.Dispose(); _readiness = null; _cleanupFinished = true;
    }

    private ExecutionTargetReadiness Capture(CancellationToken cancellation)
    {
        _readiness?.Dispose(); _readiness = null;
        try
        {
            _readiness = PcTargetReadiness.Capture(_image, cancellation, _mode);
            _readiness.RequireExpected(_image, _mode);
            return _readiness.State;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return ExecutionTargetReadiness.Unknown; }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { return ExecutionTargetReadiness.Unknown; }
    }

    private async Task StopOwnedAsync()
    {
        if (_targetStopped || _process is null) return;
        await _process.StopAsync(_stopTimeout).ConfigureAwait(false); _targetStopped = true;
    }

    private ExecutionStopReason DeferStop(ExecutionStopReason reason)
    {
        _outcome = TargetExecutionOutcome.StoppedByRequest; return reason;
    }

    private void Record(TargetExecutionOutcome outcome, string? reason = null)
    {
        _outcome = outcome; _reason = reason ?? PcStageCodec.Reason(outcome);
        _proof = new(this, _binding, outcome, _reason);
        _store.RecordStage(_proof);
    }

    internal T UseProof<T>(PreparedPcStage proof, PcStageBinding binding,
        Func<PcStageDocument, T> callback)
    {
        lock (_gate)
        {
            Verify();
            if (!ReferenceEquals(_proof, proof) || binding != _binding || _outcome is null || _reason is null)
                throw new IOException("execution_target_stage_proof_unbound");
            T CaptureDocument(ImmutableArray<ProcessIdentity> members)
            {
                var job = _process is null ? null : _store.CurrentForStage(_step.StepId);
                _frozenDocument ??= new(PcStageCodec.Format + "-evidence", 1, _binding, _outcome.Value, _reason,
                    _step.Target!.Pc!.ReadinessMode, members, job?.Binding, job?.Evidence, _elapsed.ElapsedMilliseconds);
                return callback(_frozenDocument);
            }
            if (_frozenDocument is not null) return callback(_frozenDocument);
            if (_outcome == TargetExecutionOutcome.ExternalReady)
            {
                _readiness!.RequireExpected(_image, _mode);
                return _readiness.Use(CaptureDocument);
            }
            if (_outcome == TargetExecutionOutcome.OwnedReady)
                return _process!.UseReady(_readiness!, CaptureDocument);
            return CaptureDocument([]);
        }
    }

    private ExecutionStopReason? ReadStop(CancellationToken hostStopping)
    {
        var close = AcceptedClose();
        if (close is not null) return close.ReasonCode switch
        {
            DispatchIntentCodec.Reason => ExecutionStopReason.Cancelled,
            DispatchIntentCodec.BudgetReason => ExecutionStopReason.TotalBudgetExhausted,
            _ => ExecutionStopReason.Interrupted
        };
        if (hostStopping.IsCancellationRequested) return ExecutionStopReason.Interrupted;
        return _accepted.Prepared.TimeBudget.Read().Remaining == TimeSpan.Zero ? ExecutionStopReason.TotalBudgetExhausted : null;
    }

    private ExecutionCancellationIntent? AcceptedClose()
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        return ExecutionCancellationRows.Read(connection, transaction, _parent, _attempt);
    }

    private void Verify()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); _lease.VerifyHeld(_instance); _accepted.Prepared.RequireLive();
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (!_cleanupFinished) throw new IOException("execution_target_stage_collection_pending");
        _process?.Dispose(); _readiness?.Dispose(); _captureReadiness?.Dispose(); _store.Dispose(); _disposed = true;
    }
}
