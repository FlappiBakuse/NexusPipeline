using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

[SupportedOSPlatform("windows")]
internal sealed class ManualWorkStore
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly ContractReader _contracts;
    private readonly TimeProvider _clock;
    private readonly ManualWorkCodec _codec;

    internal ManualWorkStore(RuntimeInstance instance, StateDatabase database, InstanceLease lease, ResourceLeaseManager resources,
        ContractReader contracts, TimeProvider clock)
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot) throw ManualWorkCodec.Invalid();
        _instance = instance; _database = database; _lease = lease; _resources = resources; _contracts = contracts; _clock = clock;
        _codec = new(instance, contracts);
        ManualWorkInventory.Visit(instance, database, contracts, _ => { });
    }

    internal ManualWorkState? Lookup(Guid operationId, string caller, string extensionId, string providerId, Guid taskId, long revision)
    {
        _lease.VerifyHeld(_instance);
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(caller) || caller.Length > 256) throw new ArgumentException("manual_work_query_invalid");
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = connection.BeginTransaction(deferred: true);
        if (ManualWorkRows.Find(connection, transaction, operationId) is not { } reference)
        {
            using var occupied = connection.CreateCommand(); occupied.Transaction = transaction;
            occupied.CommandText = "SELECT EXISTS(SELECT 1 FROM main.operations WHERE operation_id=$id);";
            occupied.Parameters.AddWithValue("$id", operationId.ToString("D"));
            if ((long)occupied.ExecuteScalar()! != 0) throw new OperationConflictException();
            return null;
        }
        var state = ManualWorkRows.Read(_instance, _contracts, connection, transaction, reference); var source = state.Source.Document;
        if (source.CallerSubject != caller || source.ExtensionId != extensionId || source.ProviderId != providerId ||
            source.Task.TaskId != taskId || source.Task.Revision != revision) throw new OperationConflictException();
        return state;
    }

    internal AcceptedManualWork Accept(ObservedManualTask observed, Guid operationId, string caller, Guid clientId,
        DateTimeOffset startedAt, ExecutionTimeBudget budget, Action requireCaller)
    {
        _lease.VerifyHeld(_instance); requireCaller();
        using var admission = observed.Entry.EnterTask(observed.Task.TaskId);
        using var root = _resources.TryAcquire(operationId, AcceptedManualWork.Claims(observed.Entry.ExtensionId)).Lease ??
            throw new IOException("manual_work_admission_blocked");
        return _lease.PublishExecution(() =>
        {
            requireCaller(); observed.Entry.Use(() => true); admission.Cancellation.ThrowIfCancellationRequested();
            if (budget.Read().IsExhausted) throw new IOException("manual_work_deadline");
            var invocation = ManualRunPolicy.Freeze(_instance.InstanceId, operationId, observed.Entry.ExtensionId, observed.Entry.ProviderId,
                observed.Task, startedAt);
            var observedAt = _clock.GetUtcNow();
            var document = new ManualWorkDocument("nexus.manual.work-source", 1, _instance.InstanceId, operationId, caller, clientId,
                observed.Entry.ExtensionId, observed.Entry.ProviderId, observed.Entry.ActivationId, _lease.StartupGeneration, startedAt, observedAt,
                WorkCalendar.Date(observedAt, _clock.LocalTimeZone), _clock.LocalTimeZone.Id,
                observed.Task, invocation, JournalWriterStore.CurrentWriter, budget.Read().ConsumedMilliseconds);
            _codec.Require(document);
            var source = new ManualWorkSnapshot(document, _codec.Persist(document));
            var accepted = new AcceptedManualWork(this, source, observed.Entry, budget, root.AcceptRun(_resources, operationId, invocation.RunId));
            try
            {
                _database.Write((connection, transaction) =>
                {
                    requireCaller(); admission.Cancellation.ThrowIfCancellationRequested(); observed.Entry.Use(() => true);
                    if (budget.Read().IsExhausted) throw new IOException("manual_work_deadline");
                    ManualWorkRows.Insert(connection, transaction, source, _clock.GetUtcNow()); return true;
                });
                return accepted;
            }
            catch { accepted.Dispose(); throw; }
        });
    }

    internal ManualWorkState Read(AcceptedManualWork work)
    {
        RequireOwner(work);
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = connection.BeginTransaction(deferred: true);
        return ManualWorkRows.Read(_instance, _contracts, connection, transaction, work.Source.Reference);
    }

    internal ResourceOperationLease AcquireRoot(AcceptedManualWork work)
    {
        RequireOwner(work);
        return _resources.TryContinueRun(work.Activity, work.Source.Document.OperationId,
            AcceptedManualWork.MetadataClaims(work.Entry.ExtensionId)).Lease ?? throw new IOException("manual_work_admission_blocked");
    }

    internal void Begin(AcceptedManualWork work)
    {
        RequireOwner(work); work.Entry.Use(() => true); work.Cancellation.Token.ThrowIfCancellationRequested();
        using var root = AcquireRoot(work);
        _lease.PublishExecution(() =>
        {
            work.Entry.Use(() => true); work.Cancellation.Token.ThrowIfCancellationRequested();
            var state = Read(work);
            if (state.Begin is not null || state.Result is not null) throw new IOException("manual_work_already_dispatched");
            var observed = _clock.GetUtcNow(); var counter = work.Budget.Read();
            if (counter.IsExhausted || observed < work.Source.Document.ObservedAtUtc) throw new IOException("manual_work_deadline");
            var begin = new ManualWorkBegin("nexus.manual.work-begin", 1, work.Source.Reference, observed, counter.ConsumedMilliseconds);
            var reference = _codec.Persist(begin);
            ManualWorkCodec.Require(state with { Begin = begin, BeginReference = reference });
            _database.Write((connection, transaction) =>
            {
                work.Entry.Use(() => true); work.Cancellation.Token.ThrowIfCancellationRequested();
                if (work.Budget.Read().IsExhausted || _clock.GetUtcNow() < work.Source.Document.ObservedAtUtc) throw new IOException("manual_work_deadline");
                ManualWorkRows.Append(connection, transaction, work.Source, "begin", reference); return true;
            });
        });
    }

    internal ManualWorkState Complete(AcceptedManualWork work, ScheduledBusinessResult? returned, string reason)
    {
        RequireOwner(work);
        using var root = AcquireRoot(work);
        return _lease.PublishExecution(() =>
        {
            var state = Read(work);
            if (state.Result is not null) return state;
            var observed = _clock.GetUtcNow(); var counter = work.Budget.Read();
            var regressed = observed < state.Source.Document.ObservedAtUtc || state.Begin is { } begin && observed < begin.ObservedAtUtc;
            if (state.Begin is null || counter.IsExhausted || regressed || work.Cancellation.IsCancellationRequested) returned = null;
            var result = new ManualWorkResult("nexus.manual.work-result", 1, state.Source.Reference, state.BeginReference,
                state.Begin is null ? "notStarted" : returned is null ? "unknown" : "returned", returned, counter.ConsumedMilliseconds,
                observed, regressed, reason);
            var reference = _codec.Persist(result);
            ManualWorkCodec.Require(state with { Result = result, ResultReference = reference });
            return _database.Write((connection, transaction) =>
            {
                var current = ManualWorkRows.Read(_instance, _contracts, connection, transaction, state.Source.Reference);
                if (current.Result is not null) return current;
                if (current.BeginReference != state.BeginReference) throw ManualWorkCodec.Invalid();
                ManualWorkRows.Append(connection, transaction, work.Source, "result", reference);
                return current with { Result = result, ResultReference = reference };
            });
        });
    }

    internal bool ReconcilePriorOwners()
    {
        _lease.VerifyHeld(_instance);
        var pending = new List<ManualWorkState>(); var verified = true;
        ManualWorkInventory.Visit(_instance, _database, _contracts, state => { if (state.Result is null) pending.Add(state); });
        foreach (var state in pending)
        {
            if (WriterStopEvidence.ObserveProcess(state.Source.Document.Creator.Process) != WriterState.Stopped)
            { verified = false; continue; }
            using var root = _resources.TryAcquire(Guid.NewGuid(), [new("scheduling:state", ResourceClaimMode.Mutation)]).Lease ??
                throw new IOException("manual_work_admission_blocked");
            _lease.PublishExecution(() =>
            {
                if (WriterStopEvidence.ObserveProcess(state.Source.Document.Creator.Process) != WriterState.Stopped)
                    throw new IOException("manual_work_owner_unconfirmed");
                var observed = _clock.GetUtcNow();
                var result = new ManualWorkResult("nexus.manual.work-result", 1, state.Source.Reference, state.BeginReference,
                    state.Begin is null ? "notStarted" : "unknown", null, null, observed,
                    observed < state.Source.Document.ObservedAtUtc || state.Begin is { } begin && observed < begin.ObservedAtUtc,
                    "manual_work_owner_lost");
                var reference = _codec.Persist(result);
                ManualWorkCodec.Require(state with { Result = result, ResultReference = reference });
                _database.Write((connection, transaction) =>
                {
                    var current = ManualWorkRows.Read(_instance, _contracts, connection, transaction, state.Source.Reference);
                    if (current.Result is not null) return true;
                    if (current.BeginReference != state.BeginReference) throw ManualWorkCodec.Invalid();
                    ManualWorkRows.Append(connection, transaction, state.Source, "result", reference); return true;
                });
            });
        }
        return verified;
    }

    private void RequireOwner(AcceptedManualWork work)
    {
        _lease.VerifyHeld(_instance); work.RequireOwner(this);
        if (work.Source.Document.Creator != JournalWriterStore.CurrentWriter || work.Source.Document.OwnerGeneration != _lease.StartupGeneration)
            throw ManualWorkCodec.Invalid();
    }
}
