using System.Text;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionRetryRows
{
    internal static void InsertInside(SqliteConnection connection, SqliteTransaction transaction, ExecutionRetryIntent value,
        RunBudgetObservation budget)
    {
        _ = RetryIntentCodec.Decode(RetryIntentCodec.Encode(value));
        var f = value.Frozen;
        if (budget.RunId != f.RunId || budget.JournalId != value.PreviousJournalId || budget.TerminalReceiptDigest != f.PreviousReceiptDigest ||
            budget.ConsumedMilliseconds != f.ConsumedMilliseconds) throw RetryIntentCodec.Invalid();
        RequirePrevious(connection, transaction, value);
        var identity = RetryIntentCodec.Identity(value);
        if (!OperationRows.Accept(connection, transaction, identity, budget.ObservedAtUtc).IsNew) throw RetryIntentCodec.Invalid();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO execution_retry_intents(operation_id,run_id,previous_attempt_id,previous_journal_id,next_operation_id,next_attempt_id,next_journal_id,next_ordinal,document_json) VALUES($id,$run,$prior,$journal,$next,$attempt,$nextJournal,$ordinal,$json);";
        command.Parameters.AddWithValue("$id", identity.OperationId.ToString("D")); command.Parameters.AddWithValue("$run", f.RunId.ToString("D"));
        command.Parameters.AddWithValue("$prior", f.PreviousAttemptId.ToString("D")); command.Parameters.AddWithValue("$journal", value.PreviousJournalId.ToString("D"));
        command.Parameters.AddWithValue("$next", f.NextOperationId.ToString("D")); command.Parameters.AddWithValue("$attempt", f.NextAttemptId.ToString("D"));
        command.Parameters.AddWithValue("$nextJournal", f.NextJournalId.ToString("D")); command.Parameters.AddWithValue("$ordinal", f.NextOrdinal);
        command.Parameters.AddWithValue("$json", Encoding.UTF8.GetString(RetryIntentCodec.Encode(value))); command.ExecuteNonQuery();
        command.CommandText = "UPDATE operations SET status='completed' WHERE operation_id=$id;"; command.ExecuteNonQuery();
    }

    internal static ExecutionRetryIntent Read(SqliteConnection connection, SqliteTransaction transaction, Guid operationId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT r.document_json,r.run_id,r.previous_attempt_id,r.previous_journal_id,r.next_operation_id,r.next_attempt_id,r.next_journal_id,r.next_ordinal,o.subject_id,o.request_digest,o.operation_kind,o.status,o.initial_journal_id FROM execution_retry_intents r JOIN operations o ON o.operation_id=r.operation_id WHERE r.operation_id=$id;";
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        ExecutionRetryIntent value;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw RetryIntentCodec.Invalid();
            value = RetryIntentCodec.Decode(Encoding.UTF8.GetBytes(reader.GetString(0)));
            var f = value.Frozen; var identity = RetryIntentCodec.Identity(value);
            if (identity.OperationId != operationId || reader.GetString(1) != f.RunId.ToString("D") ||
                reader.GetString(2) != f.PreviousAttemptId.ToString("D") || reader.GetString(3) != value.PreviousJournalId.ToString("D") ||
                reader.GetString(4) != f.NextOperationId.ToString("D") || reader.GetString(5) != f.NextAttemptId.ToString("D") ||
                reader.GetString(6) != f.NextJournalId.ToString("D") || reader.GetInt32(7) != f.NextOrdinal ||
                reader.GetString(8) != identity.SubjectId || reader.GetString(9) != identity.RequestDigest ||
                reader.GetString(10) != identity.Kind || reader.GetString(11) != "completed" || !reader.IsDBNull(12))
                throw RetryIntentCodec.Invalid();
        }
        RequirePrevious(connection, transaction, value);
        RunBudgetObservation? observed = null;
        _ = RunBudgetRows.Read(connection, transaction, value.Frozen.RunId, item =>
        { if (item.JournalId == value.PreviousJournalId && item.TerminalReceiptDigest == value.Frozen.PreviousReceiptDigest) observed = item; });
        if (observed is null || observed.ConsumedMilliseconds != value.Frozen.ConsumedMilliseconds) throw RetryIntentCodec.Invalid();
        return value;
    }

    internal static IReadOnlyList<Guid> Waiting(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT r.operation_id FROM execution_retry_intents r LEFT JOIN operations n ON n.operation_id=r.next_operation_id LEFT JOIN execution_retry_outcomes d ON d.retry_operation_id=r.operation_id WHERE n.operation_id IS NULL AND d.operation_id IS NULL ORDER BY json_extract(r.document_json,'$.frozen.notBefore'),r.run_id,r.next_ordinal LIMIT 64;";
        var ids = new List<Guid>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(Guid.ParseExact(reader.GetString(0), "D"));
        return ids.AsReadOnly();
    }

    internal static ExecutionRetryIntent? After(SqliteConnection connection, SqliteTransaction transaction, Guid attemptId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT operation_id FROM execution_retry_intents WHERE previous_attempt_id=$attempt;";
        command.Parameters.AddWithValue("$attempt", attemptId.ToString("D"));
        return command.ExecuteScalar() is string id ? Read(connection, transaction, Guid.ParseExact(id, "D")) : null;
    }

    private static void RequirePrevious(SqliteConnection connection, SqliteTransaction transaction, ExecutionRetryIntent value)
    {
        var f = value.Frozen;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM attempt_receipts r JOIN operations o ON o.operation_id=r.operation_id WHERE r.attempt_id=$attempt AND r.run_id=$run AND r.ordinal=$ordinal AND r.journal_id=$journal AND r.operation_id=$operation AND r.source_receipt_digest=$digest AND o.subject_id=$subject AND o.status='completed');";
        command.Parameters.AddWithValue("$attempt", f.PreviousAttemptId.ToString("D")); command.Parameters.AddWithValue("$run", f.RunId.ToString("D"));
        command.Parameters.AddWithValue("$ordinal", f.NextOrdinal - 1); command.Parameters.AddWithValue("$journal", value.PreviousJournalId.ToString("D"));
        command.Parameters.AddWithValue("$operation", value.PreviousOperationId.ToString("D")); command.Parameters.AddWithValue("$digest", f.PreviousReceiptDigest);
        command.Parameters.AddWithValue("$subject", value.SubjectId);
        if ((long)command.ExecuteScalar()! != 1) throw RetryIntentCodec.Invalid();
    }

    internal static void RequireExplicit(SqliteConnection connection, SqliteTransaction transaction, Guid nextOperation,
        ExecutionRetryIntent? retry)
    {
        var saved = ExecutionRetryRows.ForNext(connection, transaction, nextOperation);
        if (saved is null ? retry is not null : retry is null || saved != RetryIntentCodec.Identity(retry).OperationId)
            throw RetryIntentCodec.Invalid();
    }

    internal static Guid? ForNext(SqliteConnection connection, SqliteTransaction transaction, Guid nextOperationId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT operation_id FROM execution_retry_intents WHERE next_operation_id=$next;";
        command.Parameters.AddWithValue("$next", nextOperationId.ToString("D"));
        return command.ExecuteScalar() is string text ? Guid.ParseExact(text, "D") : null;
    }
}
