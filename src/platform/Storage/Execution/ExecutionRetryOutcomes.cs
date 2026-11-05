using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionRetryOutcomes
{
    internal static string? ReadReason(SqliteConnection connection, SqliteTransaction transaction, ExecutionRetryIntent retry)
    {
        var retryId = RetryIntentCodec.Identity(retry).OperationId;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT r.operation_id,r.reason_code,o.subject_id,o.request_digest,o.operation_kind,o.status,o.initial_journal_id FROM execution_retry_outcomes r JOIN operations o ON o.operation_id=r.operation_id WHERE r.retry_operation_id=$id;";
        command.Parameters.AddWithValue("$id", retryId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var identity = Identity(retry, reader.GetString(1));
        if (reader.GetString(0) != identity.OperationId.ToString("D") || reader.GetString(2) != identity.SubjectId ||
            reader.GetString(3) != identity.RequestDigest || reader.GetString(4) != identity.Kind ||
            reader.GetString(5) != "completed" || !reader.IsDBNull(6)) throw RetryIntentCodec.Invalid();
        return reader.GetString(1);
    }

    internal static bool HasOutcome(SqliteConnection connection, SqliteTransaction transaction, ExecutionRetryIntent retry) =>
        ReadReason(connection, transaction, retry) is not null;

    internal static void RequireWaiting(SqliteConnection connection, SqliteTransaction transaction, ExecutionRetryIntent retry)
    {
        if (HasOutcome(connection, transaction, retry)) throw new IOException("execution_retry_declined");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM operations WHERE operation_id=$id);";
        command.Parameters.AddWithValue("$id", retry.Frozen.NextOperationId.ToString("D"));
        if ((long)command.ExecuteScalar()! != 0) throw new IOException("execution_retry_already_accepted");
    }

    internal static void DeclineInside(SqliteConnection connection, SqliteTransaction transaction,
        ExecutionRetryIntent retry, string reason, DateTimeOffset now)
    {
        _ = ExecutionRetryRows.Read(connection, transaction, RetryIntentCodec.Identity(retry).OperationId);
        var identity = Identity(retry, reason);
        if (OperationRows.ReadExisting(connection, transaction, identity) is not null)
        {
            if (!HasOutcome(connection, transaction, retry)) throw RetryIntentCodec.Invalid();
            return;
        }
        RequireWaiting(connection, transaction, retry);
        _ = OperationRows.Accept(connection, transaction, identity, now);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO execution_retry_outcomes(retry_operation_id,operation_id,reason_code) VALUES($retry,$id,$reason);";
        command.Parameters.AddWithValue("$retry", RetryIntentCodec.Identity(retry).OperationId.ToString("D"));
        command.Parameters.AddWithValue("$id", identity.OperationId.ToString("D")); command.Parameters.AddWithValue("$reason", reason);
        command.ExecuteNonQuery();
        command.CommandText = "UPDATE operations SET status='completed' WHERE operation_id=$id;";
        if (command.ExecuteNonQuery() != 1 || !HasOutcome(connection, transaction, retry)) throw RetryIntentCodec.Invalid();
    }

    private static OperationIdentity Identity(ExecutionRetryIntent retry, string reason)
    {
        if (reason is not ("retry_budget_unknown" or "retry_budget_exhausted" or "retry_sources_unavailable" or
            "retry_authority_revoked" or "retry_run_cancelled")) throw RetryIntentCodec.Invalid();
        var source = RetryIntentCodec.Identity(retry);
        var bytes = Encoding.UTF8.GetBytes(source.OperationId.ToString("D") + "/" + source.RequestDigest + "/" + reason);
        return new(RetryIntentCodec.Id(retry.Frozen.RunId, retry.Frozen.NextOrdinal, "decline"), retry.SubjectId,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), "execution.retry.decline");
    }
}
