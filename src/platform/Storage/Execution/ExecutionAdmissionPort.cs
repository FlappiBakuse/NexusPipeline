using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Scheduling;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class ExecutionAdmissionPort : IExecutionAdmissionPort
{
    private readonly object _owner = new();
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly RecoveryIndex _index;
    private readonly ExecutionAdmissionInbox _inbox;
    private readonly ExecutionPlanBuilder _builder;
    private readonly RunAttemptAcceptance _acceptance;
    private readonly RecoveryCoordinator _publication;
    private readonly PayloadApplyRoots _roots;
    private readonly IReadOnlyDictionary<Guid, PreparedMuMuDiscovery>? _discoveries;
    private readonly PreparedExecutionOrigin? _scheduledOrigin;
    private readonly PreparedExecutionRetry? _retryOrigin;

    internal ExecutionAdmissionPort(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        ResourceLeaseManager resources, RecoveryIndex index, ExecutionAdmissionInbox inbox, ExecutionPlanBuilder builder,
        IAttemptPersistenceObserver? observer, PayloadApplyRoots roots)
    {
        _instance = instance; _database = database; _contracts = contracts; _lease = lease; _resources = resources;
        _index = index; _inbox = inbox; _builder = builder; _acceptance = new(instance, contracts, database, observer);
        _publication = new(new RecoveryJournalRegistry(database), new AdmissionPublicationEvidence(instance, database, contracts, roots), index);
        _roots = roots;
    }

    private ExecutionAdmissionPort(ExecutionAdmissionPort source, IReadOnlyDictionary<Guid, PreparedMuMuDiscovery>? discoveries,
        PreparedExecutionOrigin? scheduledOrigin = null, PreparedExecutionRetry? retryOrigin = null)
    {
        _instance = source._instance; _database = source._database; _contracts = source._contracts; _lease = source._lease;
        _resources = source._resources; _index = source._index; _inbox = source._inbox; _builder = source._builder;
        _acceptance = source._acceptance; _publication = source._publication;
        _roots = source._roots;
        _scheduledOrigin = scheduledOrigin ?? source._scheduledOrigin;
        _retryOrigin = retryOrigin ?? source._retryOrigin;
        _discoveries = discoveries?.ToDictionary(item => item.Key, item => item.Value);
        if (_discoveries is not null && _discoveries.Any(item => item.Key == Guid.Empty || item.Value is null))
            throw new ArgumentException("execution_mumu_discovery_input_invalid");
    }

    internal ExecutionAdmissionPort WithTargetDiscoveries(IReadOnlyDictionary<Guid, PreparedMuMuDiscovery> discoveries)
    {
        ArgumentNullException.ThrowIfNull(discoveries);
        return new(this, discoveries);
    }

    internal ExecutionAdmissionPort WithScheduledOrigin(PreparedExecutionOrigin origin) => new(this, _discoveries, origin);

    internal ExecutionAdmissionPort WithRetry(PreparedExecutionRetry retry) => new(this, _discoveries, retryOrigin: retry);

    public OperationIdentity Identity(Guid operationId, string subject, ExecutionSelectionRequest request)
    {
        _lease.VerifyHeld(_instance);
        _retryOrigin?.RequireRequest(operationId, subject, request, false);
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.Configuration | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var transaction = connection.BeginTransaction(deferred: true);
        ExecutionRetryRows.RequireExplicit(connection, transaction, operationId, _retryOrigin?.Intent);
        transaction.Commit();
        var identity = ExecutionStartIdentity.Create(operationId, subject, request);
        if (_retryOrigin is null && _acceptance.LookupStart(identity) is null)
            new InitialConfigurationSnapshots(_instance, _database, _contracts, _lease, _resources).Establish(subject, request, DateTimeOffset.UtcNow);
        return identity;
    }
    public IExecutionAdmissionReceipt? Lookup(OperationIdentity identity)
    {
        _lease.VerifyHeld(_instance);
        var receipt = _acceptance.LookupStart(identity);
        if (receipt is not null) { _scheduledOrigin?.RequireReceipt(receipt, _contracts); _retryOrigin?.RequireReceipt(receipt); }
        return receipt;
    }

    public IExecutionAdmissionDraft Prepare(ExecutionSelectionRequest request, DateTimeOffset createdAt)
    {
        var started = TimeProvider.System.GetTimestamp();
        var firstPreparedAt = _retryOrigin?.FirstPreparedAtUtc ?? (_scheduledOrigin is null ? TimeProvider.System.GetUtcNow() :
            _lease.RunBudgets.PreparedAt(_database, _scheduledOrigin.RunId) ?? TimeProvider.System.GetUtcNow());
        if (_scheduledOrigin is not null || _retryOrigin is not null) createdAt = firstPreparedAt;
        var draft = _retryOrigin is not null ? _builder.PrepareRetry(_retryOrigin) : _scheduledOrigin is not null ? _builder.PrepareScheduled(request, createdAt, _scheduledOrigin, _discoveries) :
            _discoveries is null ? _builder.Prepare(request, createdAt) :
            _builder.PrepareWithDiscoveries(request, createdAt, _discoveries);
        try
        {
            var shared = _retryOrigin?.TimeBudget ?? (_scheduledOrigin is null ? null : _lease.RunBudgets.BindScheduled(_database, _resources,
                _scheduledOrigin, draft, TimeProvider.System, started, firstPreparedAt));
            return new ExecutionAdmissionDraft(_owner, draft, TimeProvider.System, started, firstPreparedAt, shared, AutomaticSource());
        }
        catch { draft.Dispose(); throw; }
    }

    private bool AutomaticSource()
    {
        if (_scheduledOrigin is not null) return true;
        if (_retryOrigin is null) return false;
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.scheduled_occurrences WHERE run_id=$run UNION ALL SELECT 1 FROM main.startup_queue_occurrences WHERE run_id=$run);";
        command.Parameters.AddWithValue("$run", _retryOrigin.Intent.Frozen.RunId.ToString("D"));
        return (long)command.ExecuteScalar()! == 1;
    }

    public IPreparedExecutionAdmission Freeze(OperationIdentity identity, IExecutionAdmissionDraft draft, ResourceOperationLease root)
        => new ExecutionAdmissionPreparation(_owner, _instance, _database, _contracts, _lease, _resources, _index, _builder, _roots).Freeze(identity, Require(draft), root, _retryOrigin);

    public IExecutionAdmissionReceipt CommitAndHandoff(OperationIdentity identity, IExecutionAdmissionDraft draft,
        IPreparedExecutionAdmission prepared, ResourceOperationLease root, DateTimeOffset acceptedAt)
        => _inbox.Commits.Run(() => Commit(identity, Require(draft), Require(prepared, identity), root, acceptedAt));

    private IExecutionAdmissionReceipt Commit(OperationIdentity identity, ExecutionAdmissionDraft draft,
        PreparedExecutionAdmission prepared, ResourceOperationLease root, DateTimeOffset acceptedAt)
    {
        _lease.VerifyHeld(_instance);
        var replay = _acceptance.LookupStart(identity);
        if (replay is not null) { _scheduledOrigin?.RequireReceipt(replay, _contracts); _retryOrigin?.RequireReceipt(replay); return replay; }
        if (!_index.Current.IsReady) throw new ExecutionSelectionException("execution_host_unavailable");
        using var inboxReservation = _inbox.Reserve(identity.OperationId);
        try
        {
            var accepted = _acceptance.AcceptStart(identity, draft.Draft, prepared.Attempt, acceptedAt, _scheduledOrigin, _retryOrigin);
            if (accepted.Acceptance.IsNew) _lease.RunBudgets.RecordAccepted(_database, prepared, root, _resources, TimeProvider.System);
            PublishVerified();
            if (accepted.Acceptance.IsNew) _inbox.Publish(identity, prepared, root, accepted);
            return accepted;
        }
        catch (Exception failure)
        {
            try { ReconcileFailure(identity); }
            catch (Exception reconciliation) { throw new AggregateException("execution_acceptance_reconciliation_required", failure, reconciliation); }
            throw;
        }
    }

    private void PublishVerified() => _lease.PublishExecution(() =>
    {
        _lease.VerifyHeld(_instance);
        _publication.VerifyAndPublish();
        ConfigurationRecoveryHolds.Publish(_database, _index, _roots);
    });

    private void ReconcileFailure(OperationIdentity identity)
    {
        _index.BlockUnknown("execution_acceptance_reconciliation_required");
        _lease.VerifyHeld(_instance);
        var accepted = _acceptance.LookupStart(identity);
        if (accepted is not null)
        {
            var journal = new RecoveryJournalRegistry(_database).ReadJournal(accepted.JournalId);
            using var work = new PreparedRecoveryHandler(_instance, _contracts, _database, id =>
            {
                var stopped = new JournalWriterStore(_database).ObserveForCurrentExecutor(id);
                return new(_instance.InstanceId, id, stopped.IsComplete, stopped.State);
            }).Prepare(journal);
            _ = work.Execute();
        }
        PublishVerified();
    }

    private ExecutionAdmissionDraft Require(IExecutionAdmissionDraft draft) => draft is ExecutionAdmissionDraft value && ReferenceEquals(value.Owner, _owner)
        ? value : throw new IOException("execution_admission_draft_unbound");

    private PreparedExecutionAdmission Require(IPreparedExecutionAdmission prepared, OperationIdentity identity)
    {
        if (prepared is not PreparedExecutionAdmission value || !ReferenceEquals(value.Owner, _owner) || value.Identity != identity)
            throw new IOException("execution_admission_preparation_unbound");
        value.RequireLive(); return value;
    }
}
