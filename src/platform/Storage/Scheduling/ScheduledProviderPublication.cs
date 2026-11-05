using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

[SupportedOSPlatform("windows")]
internal sealed class ScheduledProviderPublication(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    InstanceLease lease, ResourceLeaseManager resources, TimeProvider clock)
{
    internal AcceptedProviderInvocation? Accept(ObservedWorkPlans plans, ScheduledProviderOccurrence occurrence,
        ExecutionTimeBudget counter, DateTimeOffset expectedMinuteObservation, CancellationToken stopping, ObservedManualTask? manual = null)
    {
        Require(); stopping.ThrowIfCancellationRequested();
        if (occurrence.Task.OwnerTaskId is not null)
        {
            if (manual is null || !ReferenceEquals(manual.Entry, plans.Entry)) throw ScheduledProviderCodec.Invalid();
            ManualWorkPolicy.RequirePlan(manual.Task, occurrence.Task);
        }
        else if (manual is not null || plans.Entry.SupportsManual) throw ScheduledProviderCodec.Invalid();
        var operation = occurrence.Invocation.OccurrenceId;
        using var root = resources.TryAcquire(operation, AcceptedProviderInvocation.Claims(plans.Entry.ExtensionId)).Lease ??
            throw new IOException("schedule_admission_blocked");
        return lease.PublishExecution(() =>
        {
            var now = clock.GetUtcNow();
            if (now.Offset != TimeSpan.Zero || now.ToUnixTimeSeconds() / 60 != occurrence.TriggerUtcMinute ||
                counter.Read().IsExhausted) return null;
            plans.Entry.Use(() => true);
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                if (ScheduledProviderRows.Find(connection, transaction, operation) is { } prior)
                {
                    _ = new ScheduledProviderEvidence(instance, contracts).Read(prior, connection, transaction);
                    return null;
                }
                using var occupied = NotificationDeliveryRows.Command(connection, transaction,
                    "SELECT 1 FROM operations WHERE operation_id=$0;", operation.ToString("D"));
                if (occupied.ExecuteScalar() is not null) throw ScheduledProviderCodec.Invalid();
            }
            var codec = new ScheduledProviderCodec(instance, contracts);
            var savedPlans = codec.Persist(plans.Plans);
            var document = new ScheduledProviderDocument("nexus.schedule.provider-occurrence", 1, instance.InstanceId, operation,
                ScheduleMinuteCodec.OperationId(instance.InstanceId, occurrence.TriggerUtcMinute), occurrence.TriggerUtcMinute,
                expectedMinuteObservation, plans.Entry.ExtensionId, plans.Entry.ProviderId, plans.Entry.ActivationId,
                lease.StartupGeneration, savedPlans, plans.Plans.Revision, occurrence.Invocation, occurrence.Task,
                occurrence.LocalDate, JournalWriterStore.CurrentWriter, WorkCalendar.Date(expectedMinuteObservation, clock.LocalTimeZone),
                clock.LocalTimeZone.Id, counter.Read().ConsumedMilliseconds, manual?.Task);
            codec.Require(document);
            var source = new ScheduledProviderSnapshot(document, codec.Persist(document));
            var accepted = AcceptedProviderInvocation.Accept(this, root, resources, source, plans.Entry, counter);
            try
            {
                database.Write((connection, transaction) =>
                {
                    stopping.ThrowIfCancellationRequested(); plans.Entry.Use(() => true);
                    if (clock.GetUtcNow().ToUnixTimeSeconds() / 60 != occurrence.TriggerUtcMinute || counter.Read().IsExhausted)
                        throw new IOException("scheduled_provider_minute_elapsed");
                    ScheduledProviderRows.RequireCapacity(connection, transaction, 1);
                    ScheduledProviderRows.InsertInside(connection, transaction, source, now);
                    return true;
                });
                return accepted;
            }
            catch { accepted.Activity.Dispose(); accepted.Cancellation.Dispose(); throw; }
        });
    }

    internal bool ReconcilePriorOwners()
    {
        Require();
        var pending = new List<ScheduledProviderState>(); var known = true;
        ScheduledProviderInventory.Visit(instance, database, contracts, state =>
        { if (state.Result is null) pending.Add(state); });
        foreach (var before in pending)
        {
            if (NexusPipeline.Platform.Processes.WriterStopEvidence.ObserveProcess(before.Source.Document.Creator.Process) != WriterState.Stopped)
            { known = false; continue; }
            using var root = resources.TryAcquire(Guid.NewGuid(), [new("scheduling:state", ResourceClaimMode.Mutation)]).Lease ??
                throw new IOException("schedule_admission_blocked");
            lease.PublishExecution(() =>
            {
                if (NexusPipeline.Platform.Processes.WriterStopEvidence.ObserveProcess(before.Source.Document.Creator.Process) != WriterState.Stopped)
                    throw new IOException("scheduled_provider_owner_unconfirmed");
                using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
                using var transaction = connection.BeginTransaction(deferred: true);
                var current = new ScheduledProviderEvidence(instance, contracts).Read(before.Source.Reference, connection, transaction);
                transaction.Commit();
                if (current.Result is not null) return;
                var codec = new ScheduledProviderCodec(instance, contracts);
                var close = current.Close ?? new ScheduledProviderClose("nexus.schedule.provider-close", 1,
                    current.Source.Reference, "scheduled_provider_owner_lost");
                var closeRef = current.CloseReference ?? codec.Persist(close);
                var observed = clock.GetUtcNow();
                var result = new ScheduledProviderResult("nexus.schedule.provider-result", 1, current.Source.Reference,
                    current.BeginReference, current.Begin is null ? "notStarted" : "unknown", null, null, observed,
                    observed < current.Source.Document.ObservedAtUtc || current.Begin is not null && observed < current.Begin.ObservedAtUtc, close.ReasonCode);
                ScheduledProviderEvidence.Require(current.Source, current.BeginReference, current.Begin, close, result);
                var resultRef = codec.Persist(result);
                database.Write((write, update) =>
                {
                    ScheduledProviderRows.RequireSaved(write, update, current.Source);
                    if (ScheduledProviderRows.Leaf(write, update, current.Source.Document.OperationId, "begin") != current.BeginReference ||
                        ScheduledProviderRows.Leaf(write, update, current.Source.Document.OperationId, "close") != current.CloseReference ||
                        ScheduledProviderRows.Leaf(write, update, current.Source.Document.OperationId, "result") is not null)
                        throw ScheduledProviderCodec.Invalid();
                    ScheduledProviderRows.AppendLeafInside(write, update, current.Source, "close", closeRef);
                    ScheduledProviderRows.AppendLeafInside(write, update, current.Source, "result", resultRef);
                    return true;
                });
            });
        }
        return known;
    }

    internal ScheduledProviderState Read(AcceptedProviderInvocation invocation)
    {
        Require();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var state = new ScheduledProviderEvidence(instance, contracts).Read(invocation.Source.Reference, connection, transaction);
        if (state.Source.Document.Creator != JournalWriterStore.CurrentWriter ||
            state.Source.Document.OwnerGeneration != lease.StartupGeneration) throw ScheduledProviderCodec.Invalid();
        return state;
    }

    internal void RequireOpen(AcceptedProviderInvocation invocation, SqliteConnection connection,
        SqliteTransaction transaction, bool requireBegin)
    {
        Require();
        invocation.Use(this, () => true);
        ScheduledProviderRows.RequireOpen(connection, transaction, invocation.Source, requireBegin);
    }

    internal void Begin(AcceptedProviderInvocation invocation) => Publish(invocation, () =>
    {
        invocation.Use(this, () => true);
        var state = Read(invocation);
        if (state.Begin is not null) throw new IOException("scheduled_provider_already_dispatched");
        var observed = clock.GetUtcNow();
        if (observed < invocation.Source.Document.ObservedAtUtc) throw new IOException("scheduled_provider_clock_regressed");
        var document = new ScheduledProviderBegin("nexus.schedule.provider-begin", 1, invocation.Source.Reference,
            observed, invocation.Budget.Read().ConsumedMilliseconds);
        var reference = new ScheduledProviderCodec(instance, contracts).Persist(document);
        database.Write((connection, transaction) =>
        {
            RequireOpen(invocation, connection, transaction, requireBegin: false);
            ScheduledProviderRows.AppendLeafInside(connection, transaction, invocation.Source, "begin", reference);
            return true;
        });
        return true;
    });

    internal void Close(AcceptedProviderInvocation invocation, string reason) => Publish(invocation, () =>
    {
        var state = Read(invocation);
        if (state.Result is not null || state.Close is not null) return true;
        var close = new ScheduledProviderClose("nexus.schedule.provider-close", 1, invocation.Source.Reference, reason);
        ScheduledProviderEvidence.Require(invocation.Source, state.BeginReference, state.Begin, close, null);
        var reference = new ScheduledProviderCodec(instance, contracts).Persist(close);
        database.Write((connection, transaction) =>
        {
            ScheduledProviderRows.AppendLeafInside(connection, transaction, invocation.Source, "close", reference); return true;
        });
        return true;
    });

    internal ScheduledProviderState Complete(AcceptedProviderInvocation invocation, ScheduledBusinessResult? returned) =>
        Publish(invocation, () =>
        {
            var state = Read(invocation);
            if (state.Result is not null) return state;
            if (state.Close is null && returned is not null)
            {
                try { invocation.Use(this, () => true); }
                catch (IOException)
                { returned = null; }
                catch (InvalidOperationException)
                { returned = null; }
            }
            else returned = null;
            var completion = state.Begin is null ? "notStarted" : returned is null ? "unknown" : "returned";
            if (completion == "notStarted" && state.Close is null) throw ScheduledProviderCodec.Invalid();
            var observed = clock.GetUtcNow();
            var result = new ScheduledProviderResult("nexus.schedule.provider-result", 1, invocation.Source.Reference,
                state.BeginReference, completion, returned, invocation.Budget.Read().ConsumedMilliseconds, observed,
                observed < invocation.Source.Document.ObservedAtUtc || state.Begin is not null && observed < state.Begin.ObservedAtUtc,
                returned?.ReasonCode ?? state.Close?.ReasonCode ?? "scheduled_provider_callback_unverified");
            ScheduledProviderEvidence.Require(invocation.Source, state.BeginReference, state.Begin, state.Close, result);
            var reference = new ScheduledProviderCodec(instance, contracts).Persist(result);
            database.Write((connection, transaction) =>
            {
                ScheduledProviderRows.RequireSaved(connection, transaction, invocation.Source);
                if (ScheduledProviderRows.Leaf(connection, transaction, invocation.Source.Document.OperationId, "close") != state.CloseReference)
                    throw ScheduledProviderCodec.Invalid();
                ScheduledProviderRows.AppendLeafInside(connection, transaction, invocation.Source, "result", reference);
                return true;
            });
            return Read(invocation);
        });
    private T Publish<T>(AcceptedProviderInvocation invocation, Func<T> effect)
    {
        Require();
        NexusPipeline.Core.Maintenance.ResourceClaim[] claims =
            [new("scheduling:state", NexusPipeline.Core.Maintenance.ResourceClaimMode.Mutation),
                new("extension:" + invocation.Entry.ExtensionId, NexusPipeline.Core.Maintenance.ResourceClaimMode.Reference)];
        using var root = resources.TryContinueRun(invocation.Activity, invocation.Source.Document.OperationId, claims).Lease ??
            throw new IOException("schedule_admission_blocked");
        using var held = root.Borrow(resources, invocation.Source.Document.OperationId, claims);
        return held.Use(() => lease.PublishExecution(effect));
    }

    private void Require()
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath, Path.Combine(instance.RuntimeRoot, "state", "nexus.db")))
            throw ScheduledProviderCodec.Invalid();
    }
}
