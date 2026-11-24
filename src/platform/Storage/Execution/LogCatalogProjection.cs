using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

public sealed class LogCatalogProjection(RuntimeInstance instance, StateDatabase database,
    ContractReader contracts, InstanceLease lease) : IAttemptTerminalObserver
{
    public void OnCommitted(AttemptReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt); lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath, Path.Combine(instance.RuntimeRoot, "state", "runtime.db")))
            throw LogCatalogRows.Invalid();
        lease.PublishExecution(() =>
        {
            var expected = new ExecutionLogReader(instance, database, contracts).CaptureCatalog(receipt, materialize: true);
            database.Write((connection, transaction) =>
            {
                LogCatalogRows.PublishInside(connection, transaction, expected); return true;
            });
        });
    }

    public Guid? ReconcilePage(Guid? after = null, int maximum = 64, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lease.VerifyHeld(instance);
        if (after == Guid.Empty || maximum is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(maximum));
        var ids = new List<Guid>();
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT a.attempt_id FROM records.attempt_receipts a LEFT JOIN records.execution_log_catalog l ON l.attempt_id=a.attempt_id " +
                "WHERE a.attempt_id>$after AND l.attempt_id IS NULL ORDER BY a.attempt_id LIMIT $limit;";
            command.Parameters.AddWithValue("$after", after?.ToString("D") ?? ""); command.Parameters.AddWithValue("$limit", maximum);
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                var text = rows.GetString(0); var id = Guid.ParseExact(text, "D");
                if (id == Guid.Empty || id.ToString("D") != text) throw LogCatalogRows.Invalid();
                ids.Add(id);
            }
        }
        Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { kind = "startup.projection-page",
            projection = "logs", inspected = ids.Count, maximum, after }));
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnCommitted(new AttemptReceiptStore(database).Read(id) ?? throw LogCatalogRows.Invalid());
        }
        return ids.Count == maximum ? ids[^1] : null;
    }
}

internal static class LogCatalogInventory
{
    internal static void VerifyCatalog(StateDatabase database)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM records.execution_log_entries e LEFT JOIN records.execution_log_catalog c " +
            "ON c.attempt_id=e.attempt_id WHERE c.attempt_id IS NULL) OR EXISTS(SELECT 1 FROM records.execution_log_catalog c " +
            "LEFT JOIN records.attempt_receipts a ON a.attempt_id=c.attempt_id LEFT JOIN main.operation_journals j ON j.journal_id=a.journal_id " +
            "WHERE j.operation_kind IS NULL OR j.operation_kind<>'runAttempt' OR j.phase<>'committed');";
        if ((long)command.ExecuteScalar()! != 0) throw LogCatalogRows.Invalid();
    }

    internal static void VerifyAttempt(RuntimeInstance instance, StateDatabase database, ContractReader contracts, AttemptWriterState state)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var transaction = DatabaseConnections.BeginRead(connection);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM records.execution_log_catalog WHERE attempt_id=$attempt);";
        command.Parameters.AddWithValue("$attempt", state.Attempt.AttemptId);
        if ((long)command.ExecuteScalar()! == 0) return;
        var receipt = AttemptReceiptStore.ReadInside(connection, transaction, Guid.Parse(state.Attempt.AttemptId)) ?? throw LogCatalogRows.Invalid();
        var original = new ExecutionLogReader(instance, database, contracts).CaptureValidatedCatalog(receipt, state);
        LogCatalogRows.RequireSaved(connection, transaction, original);
        transaction.Commit();
    }
}
