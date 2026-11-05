using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal sealed record ExecutionCancellationIntent(OperationIdentity Identity, string Status, string ReasonCode);

internal static class ExecutionCancellationRows
{
    internal static void RequireDispatchOpen(SqliteConnection connection, SqliteTransaction transaction, Guid parentJournalId)
    {
        RequireRecognizedTargets(connection, transaction, parentJournalId);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM operations c JOIN operation_journals j ON j.journal_id=c.initial_journal_id " +
            "WHERE j.journal_id=$parent AND c.operation_id<>j.operation_id);";
        command.Parameters.AddWithValue("$parent", parentJournalId.ToString("D"));
        if ((long)command.ExecuteScalar()! != 0) throw new IOException("execution_dispatch_closed");
    }

    internal static ExecutionCancellationIntent? Read(SqliteConnection connection, SqliteTransaction transaction, RegisteredJournal parent, RunAttemptPlan plan)
    {
        RequireRecognizedTargets(connection, transaction, parent.JournalId);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT c.operation_id,c.subject_id,c.request_digest,c.operation_kind,c.status,p.subject_id,p.operation_kind,p.initial_journal_id " +
            "FROM operations c JOIN operation_journals j ON j.journal_id=c.initial_journal_id JOIN operations p ON p.operation_id=j.operation_id " +
            "WHERE j.journal_id=$parent AND c.operation_id<>j.operation_id AND c.operation_kind<>'execution.retry.cancel' LIMIT 2;";
        command.Parameters.AddWithValue("$parent", parent.JournalId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        var options = new[] { DispatchIntentCodec.Reason, DispatchIntentCodec.CompletionReason, DispatchIntentCodec.InterruptionReason,
                DispatchIntentCodec.BudgetReason, DispatchIntentCodec.TargetReason }
            .Select(reason => new ExecutionCancellationIntent(DispatchIntentCodec.Identity(Guid.Parse(row.GetString(0)), row.GetString(1),
                parent.JournalId, parent.OperationId, Guid.Parse(plan.RunId), Guid.Parse(plan.AttemptId), reason), row.GetString(4), reason))
            .Where(item => item.Identity.RequestDigest == row.GetString(2) && item.Identity.Kind == row.GetString(3)).ToArray();
        if (options.Length != 1) throw new IOException("execution_cancel_operation_unbound");
        var result = options[0]; var identity = result.Identity;
        if (row.GetString(5) != identity.SubjectId ||
            row.GetString(6) != "execution.start" || row.IsDBNull(7) || row.GetString(7) != parent.JournalId.ToString("D") || row.Read())
            throw new IOException("execution_cancel_operation_unbound");
        return result;
    }

    internal static ExecutionCancellationIntent? Verify(StateDatabase database, RegisteredJournal parent, RunAttemptPlan plan, ExecutionPlan total,
        JsonElement latest, JsonElement projected)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var row = Read(connection, transaction, parent, plan);
        var physical = DispatchIntentCodec.Read(latest, total);
        var indexed = DispatchIntentCodec.Read(projected, total);
        if (physical is not null && (row is null || !Matches(row.Identity, physical)) || row?.Status == "completed" && indexed is null)
            throw new IOException("execution_cancel_projection_unbound");
        transaction.Commit(); return row;
    }

    internal static void CompleteInside(SqliteConnection connection, SqliteTransaction transaction, DispatchCloseIntent physical)
    {
        var identity = DispatchIntentCodec.Identity(physical.CancellationOperationId, physical.SubjectId,
            physical.ParentJournalId, physical.ParentOperationId, physical.RunId, physical.AttemptId, physical.ReasonCode);
        var accepted = OperationRows.ReadExisting(connection, transaction, identity) ?? throw new IOException("execution_cancel_operation_missing");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE operations SET status='completed' WHERE operation_id=$operation AND initial_journal_id=$parent AND status=$status " +
            "AND EXISTS(SELECT 1 FROM operation_journals j JOIN operations p ON p.operation_id=j.operation_id WHERE j.journal_id=$parent " +
            "AND j.operation_id=$parentOperation AND p.operation_kind='execution.start' AND p.initial_journal_id=j.journal_id AND p.subject_id=$subject);";
        command.Parameters.AddWithValue("$operation", identity.OperationId.ToString("D")); command.Parameters.AddWithValue("$parent", physical.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$status", accepted.Status);
        command.Parameters.AddWithValue("$parentOperation", physical.ParentOperationId.ToString("D")); command.Parameters.AddWithValue("$subject", identity.SubjectId);
        if (command.ExecuteNonQuery() != 1) throw new IOException("execution_cancel_projection_unbound");
    }

    private static bool Matches(OperationIdentity identity, DispatchCloseIntent item) => identity ==
        DispatchIntentCodec.Identity(item.CancellationOperationId, item.SubjectId, item.ParentJournalId, item.ParentOperationId, item.RunId, item.AttemptId, item.ReasonCode);

    internal static void RequireRecognizedTargets(SqliteConnection connection, SqliteTransaction transaction, Guid? parentJournalId = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        if (parentJournalId is not null)
        {
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM operations WHERE operation_kind IN ('execution.cancel','execution.close') AND initial_journal_id IS NULL);";
            if ((long)command.ExecuteScalar()! != 0) throw new IOException("execution_cancel_operation_unbound");
        }
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM operations c LEFT JOIN operation_journals j ON j.journal_id=c.initial_journal_id " +
            "LEFT JOIN operations p ON p.operation_id=j.operation_id WHERE " +
            (parentJournalId is null ? "" : "c.initial_journal_id=$parent AND ") +
            "c.operation_kind IN ('execution.cancel','execution.close') AND " +
            "(j.journal_id IS NULL OR j.operation_kind<>'runAttempt' OR c.operation_id=j.operation_id OR p.operation_id IS NULL " +
            "OR p.operation_kind<>'execution.start' OR p.initial_journal_id IS NOT j.journal_id OR c.subject_id<>p.subject_id));";
        if (parentJournalId is { } parent) command.Parameters.AddWithValue("$parent", parent.ToString("D"));
        if ((long)command.ExecuteScalar()! != 0) throw new IOException("execution_cancel_operation_unbound");
    }
}
