using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class AttemptRuntimeSession : IExecutionAttemptSession, IDisposable
{
    private readonly ConfigurationSessionFactory _configurationFactory;
    private readonly RuntimeInstance _instance;
    private readonly ExecutionLiveMedia? _liveMedia;
    private readonly ExecutionProgress? _progress;
    private readonly TaskAdapterService? _adapters;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly RecoveryIndex _index;
    private readonly AcceptedExecutionAdmission _accepted;
    private readonly ITargetPersistenceObserver? _targetObserver;
    private readonly ExecutionPlan _total;
    private readonly RunAttemptPlan _attempt;
    private readonly RegisteredJournal _parent;
    private readonly AttemptTerminalStore _terminal;
    private readonly AttemptReadSession? _readSession;
    private readonly Dictionary<string, Guid> _configurationSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExecutionRuntimeStep> _steps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PreparedConfigurationRun> _currentConfigurations;
    private readonly List<PreparedConfigurationRun> _retryConfigurations = [];
    private readonly Dictionary<string, BindingRawAllowance> _rawAllowances = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Guid> _configurationCompletions = [];
    private readonly Dictionary<string, TimeSpan> _bindingStarts = new(StringComparer.Ordinal);
    private ExecutionStopReason? _stop;
    private bool _prepared, _disposed;
    internal bool IsTerminal { get; private set; }
    internal AttemptReceipt? CommittedReceipt { get; private set; }
    public IReadOnlyList<string> StepIds { get; }
    public bool HasEnteredEffect => new JournalEffectGate(_database).Read(_parent.JournalId).State == EffectGateState.Entered;

    internal AttemptRuntimeSession(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        ResourceLeaseManager resources, RecoveryIndex index, AcceptedExecutionAdmission accepted, ITargetPersistenceObserver? targetObserver = null, ExecutionLiveMedia? liveMedia = null, TaskAdapterService? adapters = null, ExecutionProgress? progress = null, ConfigurationSessionFactory? configurationFactory = null)
    {
        _configurationFactory = configurationFactory ?? new(instance, database, contracts, lease, index);
        lease.VerifyHeld(instance); accepted.Prepared.RequireLive();
        if (!resources.UsesRecoveryIndex(index) || !accepted.Root.IsActive || accepted.Root.OperationId != accepted.Identity.OperationId)
            throw new IOException("execution_runtime_ownership_unbound");
        _progress = progress; _adapters = adapters; _liveMedia = liveMedia; _instance = instance; _database = database; _contracts = contracts; _lease = lease; _resources = resources; _index = index; _accepted = accepted;
        _targetObserver = targetObserver;
        _total = accepted.Prepared.Total.Plan.ReadWire(); _attempt = accepted.Prepared.Attempt.Plan.ReadWire();
        _currentConfigurations = accepted.Prepared.Configurations.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        _parent = new(accepted.Receipt.JournalId, accepted.Identity.OperationId, JournalKind.RunAttempt, JournalPhase.Prepared, 1,
            accepted.Receipt.Plan.RelativePath, accepted.Receipt.Plan.Sha256, _total.ResourceKeys);
        _terminal = new(instance, database, contracts, lease); StepIds = Array.AsReadOnly(_total.Steps.Select(step => step.StepId).ToArray());
        _readSession = lease.BindAttemptReads(database).Start(instance, database, contracts, _parent.JournalId);
    }

    public void PrepareConfigurationGroups()
    {
        Verify(); if (_prepared) throw new IOException("execution_runtime_preparation_closed");
        if (_total.Steps.Any(step => step.EngineKind != "process" &&
            (step.EngineKind != "extensionWorker" || step.Provider is null || !_accepted.Prepared.Providers.ContainsKey(step.StepId)))) throw new IOException("execution_runtime_engine_unavailable");
        if (_total.Steps.Any(step => step.Adapter is not null) && _adapters is null) throw new IOException("execution_adapter_service_unavailable");
        PublishVerified(); _accepted.Prepared.ReleaseLogGuards(); _prepared = true;
    }

    public ExecutionStopReason? ReadStopRequest()
    {
        Verify();
        var closed = ReadClose();
        if (closed is not null) return StopReason(closed.ReasonCode, polling: true);
        return _accepted.Prepared.TimeBudget.Read().Remaining == TimeSpan.Zero ? ExecutionStopReason.TotalBudgetExhausted : null;
    }

    public ValueTask<bool> TrySkipAsync(string stepId)
    {
        Verify();
        var step = _total.Steps.Single(step => step.StepId == stepId);
        if (step.DependsOn.Count == 0 && step.MaximumSuccessfulRunsPerDay is not > 0) return ValueTask.FromResult(false);
        var skipped = new BindingSkipStore(_instance, _database, _contracts, _lease).TrySkip(_parent.JournalId, stepId);
        if (skipped) _progress?.Step(stepId, "skipped");
        return ValueTask.FromResult(skipped);
    }

    public void PrepareNextStep(string stepId)
    {
        Verify();
        _progress?.Step(stepId, "preparing");
        if (!_prepared || _steps.ContainsKey(stepId) || !_index.Current.IsReady) throw new IOException("execution_runtime_step_unavailable");
        var input = _total.Steps.Single(step => step.StepId == stepId);
        _bindingStarts.TryAdd(stepId, TimeSpan.FromMilliseconds(_accepted.Prepared.TimeBudget.Read().ConsumedMilliseconds));
        if (_currentConfigurations.TryGetValue(stepId, out var prepared))
        {
            var id = Guid.NewGuid();
            _configurationFactory.Begin(id, _accepted.Identity.SubjectId, _parent.JournalId, stepId, prepared);
            _configurationSessions.Add(stepId, id);
            PublishVerified();
        }
        var step = new ExecutionRuntimeStep(_instance, _database, _contracts, _lease, _index, _accepted, input,
            _configurationSessions.TryGetValue(stepId, out var sessionId) ? sessionId : null, _resources, _targetObserver, _liveMedia, _adapters, prepared,
            _rawAllowances.TryGetValue(stepId, out var allowance) ? allowance : _rawAllowances[stepId] = new(BindingOutputBudget.MaximumForStep(input), 0), () => BindingElapsed(stepId), _configurationFactory);
        _steps.Add(stepId, step); step.InitializeCapture();
    }

    public ValueTask<bool> TryRetryAsync(string stepId)
    {
        Verify();
        var step = Required(stepId);
        if (!step.IsCollected || !step.IsRestored) throw new IOException("execution_step_completion_pending");
        if (step.IsBindingBudgetExpired) { step.RecordBindingTimeout(); return ValueTask.FromResult(false); }
        if (ReadStopRequest() is not null) return ValueTask.FromResult(false);
        var state = _terminal.Read(_parent.JournalId);
        if (!ExecutionInvocationStore.CanRetry(state, stepId)) return ValueTask.FromResult(false);
        if (!TaskRetryEvidence.TryCapture(state, stepId, new(_instance, _contracts), out var taskRetry)) return ValueTask.FromResult(false);
        PreparedConfigurationRun? prepared = null;
        if (_currentConfigurations.TryGetValue(stepId, out var previous))
        {
            var origin = PreparedRetryOrigin.CaptureInvocation(_instance, _database, _contracts, state, _accepted.Identity.SubjectId, stepId, taskRetry);
            var input = _total.Steps.Single(item => item.StepId == stepId);
            var request = previous.Request with { SyncInitially = false, Resources = previous.Request.Resources.Select(resource =>
                resource with { OperationId = Guid.NewGuid(), JournalId = Guid.NewGuid(),
                    Tasks = resource.Tasks.Select(rule => rule with { SelectedIds = TaskRetryEvidence.Selected(input, rule, taskRetry) }).ToImmutableArray() }).ToImmutableArray() };
            prepared = new ConfigurationRunPreparation(_instance, _database, _contracts, _lease, _resources)
                .PrepareHistorical(_accepted.Root, _accepted.Identity.OperationId, _accepted.Prepared.Total.Plan, stepId, request, origin);
            _retryConfigurations.Add(prepared);
        }
        if (step.IsBindingBudgetExpired) { step.RecordBindingTimeout(); return ValueTask.FromResult(false); }
        if (!new ExecutionInvocationStore(_instance, _database, _contracts, _lease).Append(_parent.JournalId, stepId, prepared, _accepted.Prepared.TimeBudget))
            return ValueTask.FromResult(false);
        step.Dispose(); _steps.Remove(stepId); _configurationSessions.Remove(stepId);
        if (prepared is not null) _currentConfigurations[stepId] = prepared;
        PublishVerified(); return ValueTask.FromResult(true);
    }

    public void EnterEffect(string stepId) { Verify(); Required(stepId).EnterEffect(); _progress?.Step(stepId, "running"); }

    public Task<ExecutionStopReason?> ExecuteStepAsync(string stepId, CancellationToken hostStopping)
    {
        Verify();
        return Required(stepId).ExecuteAsync(ReadStopRequest, hostStopping);
    }

    public async Task CollectStepAsync(string stepId)
    {
        Verify(); _progress?.Step(stepId, "collecting");
        await Required(stepId).CollectAsync().ConfigureAwait(false);
        _progress?.Observe(stepId, Required(stepId).TaskObservations);
        _progress?.Step(stepId, "collected");
    }
    public async Task RestoreStepAsync(string stepId)
    {
        Verify(); _progress?.Step(stepId, "restoring");
        await Required(stepId).RestoreAsync(interrupted: false).ConfigureAwait(false);
        _progress?.Step(stepId, "restored");
    }

    public void FreezeDispatch(ExecutionStopReason reason)
    {
        Verify();
        if (reason == ExecutionStopReason.Cancelled)
        {
            if (ReadClose()?.ReasonCode != DispatchIntentCodec.Reason) throw new IOException("execution_cancellation_unaccepted");
            new ExecutionCancellationStore(_instance, _database, _contracts, _lease).ResumePending(_parent.JournalId);
        }
        else _terminal.FreezeDispatch(_parent.JournalId, Reason(reason));
        _stop = StopReason(ReadClose()?.ReasonCode ?? throw new IOException("execution_dispatch_open"), polling: false);
        _progress?.Phase("stopping");
    }

    public async Task CollectRemainingAsync()
    {
        Verify();
        foreach (var stepId in StepIds)
            if (_steps.TryGetValue(stepId, out var step)) await step.CollectAsync().ConfigureAwait(false);
    }

    public void CloseUndispatched()
    {
        Verify(); var stop = _stop ?? throw new IOException("execution_dispatch_open");
        if (!HasEnteredEffect)
        {
            if (stop == ExecutionStopReason.Cancelled) _terminal.PrepareCancellation(_parent.JournalId);
            else _terminal.PrepareStopped(_parent.JournalId, stop == ExecutionStopReason.Completed ? LifecycleOutcome.Skipped : stop == ExecutionStopReason.TotalBudgetExhausted ? LifecycleOutcome.Blocked : LifecycleOutcome.Interrupted, Reason(stop));
        }
        _terminal.CloseUndispatched(_parent.JournalId);
    }

    public void MarkApplied() { Verify(); _terminal.MarkApplied(_parent.JournalId); }

    public async Task RestoreRemainingAsync(ExecutionStopReason reason)
    {
        Verify(); var interrupted = (_stop ?? reason) != ExecutionStopReason.Completed;
        foreach (var stepId in StepIds)
            if (_steps.TryGetValue(stepId, out var step)) await step.RestoreAsync(interrupted).ConfigureAwait(false);
        var pending = _configurationSessions.Where(item => !_steps.TryGetValue(item.Key, out var step) || step.RequiresUnregisteredConfigurationCompletion).ToArray();
        foreach (var item in pending)
            _configurationFactory.Restore(item.Value, _accepted.Identity.SubjectId);
        PublishVerified();
        foreach (var item in pending)
        {
            new RunCompletionService(_instance, _database, _contracts, _lease, _resources, _index, _accepted.Prepared.PayloadRoots)
                .CompleteOwnedInterrupted(CompletionOperation(item.Value), _accepted.Identity.SubjectId, item.Value, _currentConfigurations[item.Key]);
            if (_steps.TryGetValue(item.Key, out var step)) step.CompleteUnregisteredConfiguration();
        }
    }

    public AttemptReceipt Commit(ExecutionStopReason reason)
    {
        Verify(); var stop = _stop ?? reason; AttemptReceipt receipt;
        using var budget = _lease.RunBudgets.PrepareTerminal(_database, _accepted, _resources, TimeProvider.System);
        if (!HasEnteredEffect)
            receipt = stop == ExecutionStopReason.Cancelled ? _terminal.CommitPreparedCancellation(_parent.JournalId, budget, ReleaseOwnership) :
                _terminal.CommitPreparedStop(_parent.JournalId, stop == ExecutionStopReason.Completed ? LifecycleOutcome.Skipped : stop == ExecutionStopReason.TotalBudgetExhausted ? LifecycleOutcome.Blocked : LifecycleOutcome.Interrupted, Reason(stop), budget, ReleaseOwnership);
        else receipt = _terminal.Commit(_parent.JournalId, stop switch
        {
            ExecutionStopReason.Completed => LifecycleOutcome.Completed, ExecutionStopReason.Cancelled => LifecycleOutcome.Cancelled,
            _ => LifecycleOutcome.Interrupted
        }, stop == ExecutionStopReason.Completed ? "execution_completed" : Reason(stop), budget, ReleaseOwnership);
        CommittedReceipt = receipt;
        return receipt;

        void ReleaseOwnership()
        {
            // Saves share this publication boundary with the visible receipt and its restored ownership.
            IsTerminal = true; VerifyPublication(); budget.Dispose(); Dispose();
        }
    }

    public async Task QuarantineFailureAsync()
    {
        if (IsTerminal) return;
        _index.BlockUnknown("execution_runtime_reconciliation_required");
        var failure = "attempt_writer_stop_unconfirmed";
        try
        {
            FreezeDispatch(ExecutionStopReason.Interrupted); await CollectRemainingAsync().ConfigureAwait(false);
            failure = "attempt_workers_uncollected"; CloseUndispatched();
            if (HasEnteredEffect) MarkApplied();
            failure = "attempt_configuration_not_restored";
            await RestoreRemainingAsync(_stop!.Value).ConfigureAwait(false); _ = Commit(_stop.Value);
        }
        catch
        {
            try
            {
                var state = _terminal.Read(_parent.JournalId);
                if (state.Gate.State == EffectGateState.Sealed && state.Latest.Phase == JournalPhase.Prepared)
                {
                    _index.BlockUnknown("execution_runtime_reconciliation_required"); return;
                }
                _terminal.Quarantine(_parent.JournalId, failure); PublishVerified();
            }
            catch { _index.BlockUnknown("execution_runtime_reconciliation_required"); }
        }
    }

    private ExecutionCancellationIntent? ReadClose()
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        return ExecutionCancellationRows.Read(connection, transaction, _parent, _attempt);
    }

    private void PublishVerified() => _lease.PublishExecution(VerifyPublication);

    private void VerifyPublication()
    {
        new RecoveryCoordinator(new RecoveryJournalRegistry(_database), new SnapshotCompletionEvidence(_instance, _database, _contracts,
            _accepted.Prepared.PayloadRoots), _index).VerifyAndPublish();
        ConfigurationRecoveryHolds.Publish(_database, _index, _accepted.Prepared.PayloadRoots);
    }
    private Guid CompletionOperation(Guid sessionId)
    {
        var prior = SnapshotIntentCodec.Read(_terminal.Read(_parent.JournalId).Latest.Root)
            .SingleOrDefault(item => item.SessionId == sessionId)?.CompletionOperationId;
        if (_configurationCompletions.TryGetValue(sessionId, out var cached))
        {
            if (prior is not null && prior != cached) throw new IOException("execution_configuration_completion_unbound");
            return cached;
        }
        var operation = prior ?? Guid.NewGuid(); _configurationCompletions.Add(sessionId, operation); return operation;
    }
    private TimeSpan BindingElapsed(string stepId)
    {
        var elapsed = TimeSpan.FromMilliseconds(_accepted.Prepared.TimeBudget.Read().ConsumedMilliseconds) - _bindingStarts[stepId];
        return elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero;
    }
    private ExecutionRuntimeStep Required(string stepId) => _steps.GetValueOrDefault(stepId) ?? throw new IOException("execution_runtime_step_unprepared");
    private void Verify() { ObjectDisposedException.ThrowIf(_disposed, this); _lease.VerifyHeld(_instance); _accepted.Prepared.RequireLive(); }
    private static string Reason(ExecutionStopReason reason) => reason switch
    {
        ExecutionStopReason.Completed => DispatchIntentCodec.CompletionReason,
        ExecutionStopReason.Cancelled => DispatchIntentCodec.Reason,
        ExecutionStopReason.Interrupted => DispatchIntentCodec.InterruptionReason,
        ExecutionStopReason.TotalBudgetExhausted => DispatchIntentCodec.BudgetReason,
        ExecutionStopReason.TargetUnavailable => DispatchIntentCodec.TargetReason,
        _ => throw new ArgumentException("execution_stop_request_invalid")
    };
    private static ExecutionStopReason StopReason(string reason, bool polling) => reason switch
    {
        DispatchIntentCodec.Reason => ExecutionStopReason.Cancelled,
        DispatchIntentCodec.BudgetReason => ExecutionStopReason.TotalBudgetExhausted,
        DispatchIntentCodec.InterruptionReason => ExecutionStopReason.Interrupted,
        DispatchIntentCodec.TargetReason => ExecutionStopReason.TargetUnavailable,
        DispatchIntentCodec.CompletionReason => polling ? ExecutionStopReason.Interrupted : ExecutionStopReason.Completed,
        _ => throw new IOException("execution_dispatch_reason_unrecognized")
    };
    public void Dispose()
    {
        if (_disposed) return;
        if (!IsTerminal) throw new IOException("execution_runtime_reconciliation_required");
        foreach (var step in _steps.Values) step.Dispose();
        foreach (var configuration in _retryConfigurations) configuration.Dispose();
        _readSession?.Dispose(); _accepted.Dispose(); _disposed = true;
    }
}
