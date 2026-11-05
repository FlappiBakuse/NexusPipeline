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
            !StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath, Path.Combine(instance.RuntimeRoot, "state", "nexus.db")))
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

    public Guid? ReconcilePage(Guid? after = null, int maximum = 64)
    {
        lease.VerifyHeld(instance);
        if (after == Guid.Empty || maximum is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(maximum));
        var ids = new List<Guid>();
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT a.attempt_id FROM attempt_receipts a LEFT JOIN execution_log_catalog l ON l.attempt_id=a.attempt_id " +
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
        foreach (var id in ids) OnCommitted(new AttemptReceiptStore(database).Read(id) ?? throw LogCatalogRows.Invalid());
        return ids.Count == maximum ? ids[^1] : null;
    }
}
