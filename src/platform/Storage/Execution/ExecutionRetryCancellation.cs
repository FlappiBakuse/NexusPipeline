using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionRetryCancellation
{
    internal const string Kind = "execution.retry.cancel";

    internal static OperationAcceptance Commit(StateDatabase database, Guid operationId, string subject,
        Guid startOperation, Guid runId, Guid attemptId, DateTimeOffset now)
    {
        if (now.Offset != TimeSpan.Zero) throw RetryIntentCodec.Invalid();
        return database.Write((connection, transaction) =>
        {
            var source = StartSourceRows.Read(connection, transaction, startOperation);
            var retry = ExecutionRetryRows.After(connection, transaction, attemptId)
                ?? throw new IOException("execution_retry_not_waiting");
            Require(retry, source, runId, attemptId, subject);
            var identity = Identity(operationId, retry);
            var prior = OperationRows.ReadExisting(connection, transaction, identity);
            if (prior is not null)
            {
                RequireCompleted(connection, transaction, identity, retry);
                return prior;
            }
            ExecutionRetryOutcomes.DeclineInside(connection, transaction, retry, "retry_run_cancelled", now);
            var accepted = OperationRows.Accept(connection, transaction, identity, now);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE operations SET initial_journal_id=$journal,status='completed' WHERE operation_id=$id;";
            command.Parameters.AddWithValue("$journal", source.JournalId.ToString("D"));
            command.Parameters.AddWithValue("$id", operationId.ToString("D"));
            if (command.ExecuteNonQuery() != 1) throw RetryIntentCodec.Invalid();
            RequireCompleted(connection, transaction, identity, retry);
            return accepted with { Status = "completed" };
        });
    }

    internal static void VerifyInventory(StateDatabase database)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT o.operation_id,r.operation_id FROM operations o LEFT JOIN execution_retry_intents r " +
            "ON r.previous_journal_id=o.initial_journal_id WHERE o.operation_kind=$kind ORDER BY o.operation_id;";
        command.Parameters.AddWithValue("$kind", Kind);
        var pairs = new List<(Guid Operation, Guid Retry)>();
        using (var rows = command.ExecuteReader()) while (rows.Read())
        {
            if (rows.IsDBNull(1)) throw RetryIntentCodec.Invalid();
            pairs.Add((Guid.ParseExact(rows.GetString(0), "D"), Guid.ParseExact(rows.GetString(1), "D")));
        }
        foreach (var pair in pairs)
        {
            var retry = ExecutionRetryRows.Read(connection, transaction, pair.Retry);
            var source = StartSourceRows.Read(connection, transaction, retry.PreviousOperationId);
            Require(retry, source, retry.Frozen.RunId, retry.Frozen.PreviousAttemptId, retry.SubjectId);
            RequireCompleted(connection, transaction, Identity(pair.Operation, retry), retry);
        }
    }

    private static void Require(ExecutionRetryIntent retry, FrozenStartSource source,
        Guid runId, Guid attemptId, string subject)
    {
        if (retry.SubjectId != subject || source.Identity.SubjectId != subject || source.RunId != runId ||
            source.JournalId != retry.PreviousJournalId || source.Identity.OperationId != retry.PreviousOperationId ||
            retry.Frozen.RunId != runId || retry.Frozen.PreviousAttemptId != attemptId ||
            retry.Frozen.NextOrdinal != source.AttemptOrdinal + 1) throw new OperationConflictException();
    }

    private static OperationIdentity Identity(Guid operationId, ExecutionRetryIntent retry)
    {
        var source = RetryIntentCodec.Identity(retry);
        var bytes = Encoding.UTF8.GetBytes(source.OperationId.ToString("D") + "/" + source.RequestDigest + "/retry_run_cancelled");
        return new(operationId, retry.SubjectId, Convert.ToHexStringLower(SHA256.HashData(bytes)), Kind);
    }

    private static void RequireCompleted(SqliteConnection connection, SqliteTransaction transaction,
        OperationIdentity identity, ExecutionRetryIntent retry)
    {
        if (OperationRows.ReadExisting(connection, transaction, identity)?.Status != "completed" ||
            ExecutionRetryOutcomes.ReadReason(connection, transaction, retry) != "retry_run_cancelled")
            throw RetryIntentCodec.Invalid();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT initial_journal_id FROM operations WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$id", identity.OperationId.ToString("D"));
        if (command.ExecuteScalar() as string != retry.PreviousJournalId.ToString("D"))
            throw RetryIntentCodec.Invalid();
    }
}
