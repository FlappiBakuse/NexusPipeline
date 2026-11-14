using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal enum ExecutionCancellationPoint { IntentAccepted, JournalFlushed }
internal interface IExecutionCancellationObserver { void Reached(ExecutionCancellationPoint point); }
// Completion here acknowledges durable dispatch closure; it does not attest that any writer has stopped.
internal sealed record ExecutionCancellationResult(OperationAcceptance Acceptance, Guid ParentJournalId, Guid RunId, Guid AttemptId, bool IsProjected);

internal sealed class ExecutionCancellationStore(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
    IExecutionCancellationObserver? observer = null)
{
    internal static OperationIdentity Identity(Guid operationId, string subject, Guid parentJournalId, Guid parentOperationId, Guid runId, Guid attemptId) =>
        DispatchIntentCodec.Identity(operationId, subject, parentJournalId, parentOperationId, runId, attemptId);

    internal ExecutionCancellationResult Cancel(OperationIdentity identity, Guid parentJournalId, DateTimeOffset acceptedAt)
        => lease.PublishExecution(() => AcceptClose(identity, parentJournalId, acceptedAt, DispatchIntentCodec.Reason));

    internal ExecutionCancellationResult Close(Guid parentJournalId, string reason)
        => lease.PublishExecution(() => CloseCore(parentJournalId, reason));

    private ExecutionCancellationResult CloseCore(Guid parentJournalId, string reason)
    {
        lease.VerifyHeld(instance);
        if (reason is not (DispatchIntentCodec.CompletionReason or DispatchIntentCodec.InterruptionReason or DispatchIntentCodec.BudgetReason or DispatchIntentCodec.TargetReason))
            throw new ArgumentException("execution_dispatch_reason_unrecognized");
        var state = AttemptWriterState.Read(instance, database, contracts, parentJournalId);
        if (state.Cancellation is { } existing)
            return AcceptClose(existing.Identity, parentJournalId, DateTimeOffset.UtcNow, existing.ReasonCode);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT subject_id FROM main.operations WHERE operation_id=$operation;";
        command.Parameters.AddWithValue("$operation", state.Journal.OperationId.ToString("D"));
        var subject = command.ExecuteScalar() as string ?? throw new IOException("execution_writer_operation_unbound");
        var identity = DispatchIntentCodec.Identity(Guid.NewGuid(), subject, parentJournalId, state.Journal.OperationId,
            Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId), reason);
        return AcceptClose(identity, parentJournalId, DateTimeOffset.UtcNow, reason);
    }

    private ExecutionCancellationResult AcceptClose(OperationIdentity identity, Guid parentJournalId, DateTimeOffset acceptedAt, string reason)
    {
        lease.VerifyHeld(instance);
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
        using (var transaction = connection.BeginTransaction(deferred: true)) _ = OperationRows.ReadExisting(connection, transaction, identity);
        if (identity.Kind != DispatchIntentCodec.Kind(reason)) throw new OperationConflictException();
        var state = AttemptWriterState.Read(instance, database, contracts, parentJournalId);
        if (identity != DispatchIntentCodec.Identity(identity.OperationId, identity.SubjectId, parentJournalId, state.Journal.OperationId,
            Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId), reason))
            throw new OperationConflictException();
        if (state.Latest.Root.GetProperty("participants").EnumerateArray().Any(item => item.GetProperty("participantId").GetGuid() == identity.OperationId &&
            !DispatchIntentCodec.IsParticipant(item))) throw new OperationConflictException();
        var accepted = database.Write((connection, transaction) =>
        {
            var existing = OperationRows.ReadExisting(connection, transaction, identity);
            if (existing is not null)
            {
                var prior = ExecutionCancellationRows.Read(connection, transaction, state.Journal, state.Attempt);
                if (prior?.Identity != identity) throw new OperationConflictException();
                return existing;
            }
            AttemptWriterState.RequireCursor(connection, transaction, state.Journal, state.Gate.State);
            if (state.Journal.Phase is not (JournalPhase.Prepared or JournalPhase.Applying) || state.Latest.Revision != state.Journal.Revision ||
                state.Gate.State == EffectGateState.Sealed)
                throw new IOException("execution_cancel_parent_unavailable");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.operations WHERE operation_id=$parentOperation AND subject_id=$subject AND status='accepted');";
            command.Parameters.AddWithValue("$parentOperation", state.Journal.OperationId.ToString("D")); command.Parameters.AddWithValue("$subject", identity.SubjectId);
            if ((long)command.ExecuteScalar()! != 1) throw new OperationConflictException();
            ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, parentJournalId);
            var result = OperationRows.Accept(connection, transaction, identity, acceptedAt);
            command.CommandText = "UPDATE main.operations SET initial_journal_id=$parent WHERE operation_id=$operation;";
            command.Parameters.AddWithValue("$parent", parentJournalId.ToString("D")); command.Parameters.AddWithValue("$operation", identity.OperationId.ToString("D"));
            if (command.ExecuteNonQuery() != 1) throw new IOException("execution_cancel_operation_unbound");
            command.CommandText = "UPDATE main.recovery_index_state SET ready=0,revision=revision+1 WHERE singleton=1;";
            if (command.ExecuteNonQuery() != 1) throw new IOException("execution_cancel_index_unbound");
            return result;
        });
        if (accepted.IsNew) observer?.Reached(ExecutionCancellationPoint.IntentAccepted);
        ResumeAcceptedCore(instance, database, contracts, parentJournalId, observer);
        return new(accepted with { Status = "completed" }, parentJournalId, Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId), true);
    }

    internal void ResumePending(Guid parentJournalId)
        => lease.PublishExecution(() => ResumeAcceptedCore(instance, database, contracts, parentJournalId, observer));

    internal static void ResumeAccepted(RuntimeInstance instance, StateDatabase database, ContractReader contracts, Guid parentJournalId,
        InstanceLease? lease, IExecutionCancellationObserver? observer = null)
    {
        var state = AttemptWriterState.Read(instance, database, contracts, parentJournalId);
        if (state.Cancellation is null || state.Cancellation.Status == "completed") return;
        (lease ?? throw new IOException("runtime_exclusive_lease_required")).PublishExecution(() =>
            ResumeAcceptedCore(instance, database, contracts, parentJournalId, observer));
    }

    private static void ResumeAcceptedCore(RuntimeInstance instance, StateDatabase database, ContractReader contracts, Guid parentJournalId,
        IExecutionCancellationObserver? observer)
    {
        var state = AttemptWriterState.Read(instance, database, contracts, parentJournalId);
        var accepted = state.Cancellation;
        if (accepted is null || accepted.Status == "completed") return;
        if (state.Latest.Phase is not (JournalPhase.Prepared or JournalPhase.Applying or JournalPhase.Applied)) throw new IOException("execution_cancel_recovery_unavailable");
        if (DispatchIntentCodec.Read(state.Latest.Root, state.Total) is null)
        {
            var item = new DispatchCloseIntent(DispatchIntentCodec.Format, 1, state.Journal.OperationId, parentJournalId,
                state.Journal.PlanDigest, Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId), accepted.Identity.OperationId,
                accepted.Identity.SubjectId, accepted.ReasonCode);
            var document = JsonNode.Parse(state.Latest.Export())!.AsObject(); document["revision"] = state.Latest.Revision + 1;
            document["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
            document["participants"]!.AsArray().Add(DispatchIntentCodec.Participant(item, state.Total.Steps[0].ScriptId));
            new JournalStore(instance, contracts).Append(parentJournalId, state.Latest.Revision, JsonSerializer.SerializeToUtf8Bytes(document));
            observer?.Reached(ExecutionCancellationPoint.JournalFlushed);
        }
        ExecutionAttemptProjection.Synchronize(instance, database, contracts, AttemptWriterState.Read(instance, database, contracts, parentJournalId));
    }

    internal static void VerifyInventory(RuntimeInstance instance, StateDatabase database, ContractReader contracts, int pageSize = 128)
    {
        if (pageSize is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(pageSize));
        VerifyTargets(database);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var transaction = connection.BeginTransaction(deferred: true);
        const string targets = "(c.operation_kind IN ('execution.cancel','execution.close') OR j.operation_kind='runAttempt' AND c.operation_id<>j.operation_id AND c.operation_kind<>'execution.retry.cancel')";
        var after = "";
        while (true)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT DISTINCT c.initial_journal_id FROM main.operations c LEFT JOIN main.operation_journals j ON j.journal_id=c.initial_journal_id WHERE " +
                targets + " AND c.initial_journal_id>$after ORDER BY c.initial_journal_id LIMIT $limit;";
            command.Parameters.AddWithValue("$after", after); command.Parameters.AddWithValue("$limit", pageSize);
            var parents = new List<Guid>();
            using (var row = command.ExecuteReader()) while (row.Read()) parents.Add(Guid.Parse(row.GetString(0)));
            foreach (var parent in parents) _ = AttemptWriterState.Read(instance, database, contracts, parent);
            if (parents.Count < pageSize) break;
            after = parents[^1].ToString("D");
        }
        transaction.Commit();
    }

    internal static void VerifyTargets(StateDatabase database)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = connection.BeginTransaction(deferred: true);
        ExecutionCancellationRows.RequireRecognizedTargets(connection, transaction);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.operations c LEFT JOIN main.operation_journals j ON j.journal_id=c.initial_journal_id " +
            "WHERE (c.operation_kind IN ('execution.cancel','execution.close') OR j.operation_kind='runAttempt' " +
            "AND c.operation_id<>j.operation_id AND c.operation_kind<>'execution.retry.cancel') " +
            "AND (c.initial_journal_id IS NULL OR j.operation_kind IS NULL OR j.operation_kind<>'runAttempt'));";
        if ((long)command.ExecuteScalar()! != 0) throw new IOException("execution_cancel_operation_unbound");
        transaction.Commit();
    }
}
