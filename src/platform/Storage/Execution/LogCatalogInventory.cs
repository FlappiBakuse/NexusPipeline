using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class LogCatalogInventory
{
    internal static void VerifyCatalog(StateDatabase database)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM execution_log_entries e LEFT JOIN execution_log_catalog c " +
            "ON c.attempt_id=e.attempt_id WHERE c.attempt_id IS NULL) OR EXISTS(SELECT 1 FROM execution_log_catalog c " +
            "LEFT JOIN attempt_receipts a ON a.attempt_id=c.attempt_id LEFT JOIN operation_journals j ON j.journal_id=a.journal_id " +
            "WHERE j.operation_kind IS NULL OR j.operation_kind<>'runAttempt' OR j.phase<>'committed');";
        if ((long)command.ExecuteScalar()! != 0) throw LogCatalogRows.Invalid();
    }

    internal static void VerifyAttempt(RuntimeInstance instance, StateDatabase database, ContractReader contracts, AttemptWriterState state)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM execution_log_catalog WHERE attempt_id=$attempt);";
        command.Parameters.AddWithValue("$attempt", state.Attempt.AttemptId);
        if ((long)command.ExecuteScalar()! == 0) return;
        var receipt = AttemptReceiptStore.ReadInside(connection, transaction, Guid.Parse(state.Attempt.AttemptId)) ?? throw LogCatalogRows.Invalid();
        var original = new ExecutionLogReader(instance, database, contracts).CaptureValidatedCatalog(receipt, state);
        LogCatalogRows.RequireSaved(connection, transaction, original);
        transaction.Commit();
    }
}
