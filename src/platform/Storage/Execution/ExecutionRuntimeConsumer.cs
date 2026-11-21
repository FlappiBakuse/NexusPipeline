using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
public sealed class ExecutionRuntimeConsumer
{
    private readonly ConfigurationSessionFactory _configurationFactory;
    private readonly RuntimeInstance _instance;
    private readonly ExecutionLiveMedia? _liveMedia;
    private TaskAdapterService? _adapters;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly RecoveryIndex _index;
    private readonly ExecutionAdmissionInbox _inbox;
    private readonly List<AttemptRuntimeSession> _reconciliation = [];
    private readonly Dictionary<Guid, ExecutionProgress> _active = [];
    private long _progressRevision;
    private readonly object _progressGate = new();
    private TaskCompletionSource? _progressChanged;
    private readonly object _gate = new();
    private readonly IAttemptTerminalObserver? _terminalObserver;
    private bool _reconciliationRequired;
    private int _scopedReconciliations;
    private int _consuming;
    private bool _started;
    public string? FailureCode { get; private set; }
    public long ProgressRevision => Interlocked.Read(ref _progressRevision);
    public Task WaitForProgressAsync(long revision, CancellationToken cancellationToken)
    {
        lock (_progressGate)
        {
            if (revision != ProgressRevision) return Task.CompletedTask;
            _progressChanged ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _progressChanged.Task.WaitAsync(cancellationToken);
        }
    }

    private void AdvanceProgress()
    {
        lock (_progressGate)
        {
            Interlocked.Increment(ref _progressRevision);
            _progressChanged?.TrySetResult();
            _progressChanged = null;
        }
    }
    public IReadOnlyList<ExecutionRunView> ReadActive(string subject)
    { lock (_gate) return _active.Values.Select(progress => progress.Read(subject)).ToArray(); }
    internal void ObserveCancellation(Guid operationId, Guid runId, Guid attemptId)
    {
        _lease.VerifyHeld(_instance);
        lock (_gate) if (_active.TryGetValue(operationId, out var progress)) progress.CancelAccepted(runId, attemptId);
    }
    internal Exception? FailureException { get; private set; }
    public int PendingReconciliationCount
    { get { lock (_gate) return (_reconciliationRequired ? Math.Max(_reconciliation.Count, 1) : 0) + _scopedReconciliations; } }
    private bool RequiresGlobalReconciliation { get { lock (_gate) return _reconciliationRequired; } }

    public ExecutionRuntimeConsumer(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        ResourceLeaseManager resources, RecoveryIndex index, ExecutionAdmissionInbox inbox, IAttemptTerminalObserver? terminalObserver = null, ExecutionLiveMedia? liveMedia = null, ConfigurationSessionFactory? configurationFactory = null)
    {
        _configurationFactory = configurationFactory ?? new(instance, database, contracts, lease, index);
        lease.VerifyHeld(instance); inbox.Bind(instance, resources, index);
        if (!resources.UsesRecoveryIndex(index)) throw new IOException("execution_runtime_recovery_index_unbound");
        _liveMedia = liveMedia; _instance = instance; _database = database; _contracts = contracts; _lease = lease; _resources = resources; _index = index; _inbox = inbox; _terminalObserver = terminalObserver;
    }

    public void ConfigureTaskAdapters(ITaskAdapterPort adapters)
    {
        if (adapters is not TaskAdapterService installed) throw new IOException("adapter_service_owner_unbound");
        installed.RequireOwner(_instance, _database, _lease, _resources);
        lock (_gate)
        {
            if (_started || _adapters is not null) throw new IOException("adapter_service_configuration_closed");
            _adapters = installed;
        }
    }

    public async Task<AttemptReceipt?> RunNextAsync(CancellationToken hostStopping = default)
    {
        Enter();
        try
        {
            var accepted = _inbox.TakeNext();
            return accepted is null ? null : await ExecuteAsync(accepted, hostStopping).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref _consuming, 0); }
    }

    public async Task RunAsync(CancellationToken hostStopping)
    {
        Enter();
        using var dispatch = CancellationTokenSource.CreateLinkedTokenSource(hostStopping);
        try
        {
            // Resource admission owns conflicts; the inbox limit bounds supervised consumers.
            var consumers = Enumerable.Range(0, _inbox.Capacity).Select(_ => ConsumeAsync()).ToArray();
            await Task.WhenAll(consumers).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref _consuming, 0); }

        async Task ConsumeAsync()
        {
            while (!RequiresGlobalReconciliation)
            {
                AcceptedExecutionAdmission? accepted;
                if (hostStopping.IsCancellationRequested)
                { accepted = _inbox.TakeNext(); if (accepted is null) return; }
                else
                {
                    try { accepted = await _inbox.WaitNextAsync(dispatch.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (dispatch.IsCancellationRequested) { continue; }
                }
                try { _ = await ExecuteAsync(accepted, hostStopping).ConfigureAwait(false); }
                catch when (FailureCode is not null)
                { if (RequiresGlobalReconciliation) { dispatch.Cancel(); return; } }
            }
        }
    }

    private async Task<AttemptReceipt> ExecuteAsync(AcceptedExecutionAdmission accepted, CancellationToken hostStopping)
    {
        AttemptRuntimeSession? session = null;
        var terminalObserved = false;
        var progress = new ExecutionProgress(accepted, AdvanceProgress);
        lock (_gate) _active.Add(accepted.Identity.OperationId, progress);
        AdvanceProgress();
        try
        {
            session = new(_instance, _database, _contracts, _lease, _resources, _index, accepted, liveMedia: _liveMedia, adapters: _adapters, progress: progress, configurationFactory: _configurationFactory);
            var receipt = await new ExecutionAttemptCoordinator().RunAsync(session, hostStopping).ConfigureAwait(false);
            ObserveTerminal(receipt);
            return receipt;
        }
        catch (Exception error)
        {
            ExecutionFailureTrace.Record(accepted, error);
            if (!terminalObserved && session?.CommittedReceipt is { } recovered)
            {
                ObserveTerminal(recovered);
                if (recovered.Facts.Lifecycle == LifecycleOutcome.Cancelled) return recovered;
            }
            FailureException = error;
            var isolated = session?.IsDurablyIsolated == true;
            FailureCode = session?.IsTerminal == true || isolated ? "execution_runtime_attempt_failed" : "execution_runtime_reconciliation_required";
            if (isolated)
            {
                progress.Phase("reconciliationRequired");
                lock (_gate) _scopedReconciliations++;
            }
            else if (session?.IsTerminal != true)
            {
                progress.Phase("reconciliationRequired");
                lock (_gate) { _reconciliationRequired = true; if (session is not null) _reconciliation.Add(session); }
                _index.BlockUnknown(FailureCode);
            }
            throw;
        }
        finally
        {
            if (session?.IsTerminal == true)
            {
                lock (_gate) _active.Remove(accepted.Identity.OperationId);
                AdvanceProgress();
                session.Dispose();
            }
            else if (session is null) accepted.Dispose();
        }

        void ObserveTerminal(AttemptReceipt receipt)
        {
            terminalObserved = true;
            try
            {
                _terminalObserver?.OnCommitted(receipt);
                var finality = new ExecutionRunFinality(_instance, _database, _contracts);
                if (finality.IsFinal(receipt)) _lease.RunBudgets.ReleaseFinal(_database, receipt, finality);
            }
            catch
            {
                _index.BlockUnknown("execution_terminal_notification_pending");
                throw;
            }
        }
    }

    private void Enter()
    {
        _lease.VerifyHeld(_instance);
        if (RequiresGlobalReconciliation) throw new IOException("execution_runtime_reconciliation_required");
        lock (_gate)
        {
            if (Interlocked.CompareExchange(ref _consuming, 1, 0) != 0) throw new IOException("execution_runtime_consumer_already_active");
            _started = true;
        }
    }
}
