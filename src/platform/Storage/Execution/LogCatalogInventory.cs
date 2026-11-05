using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class LogCatalogInventory
{
    internal static void VerifyReadOnly(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot) throw LogCatalogRows.Invalid();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var original = new ExecutionLogReader(instance, database, contracts); var after = "";
        while (true)
        {
            var page = new List<Guid>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT attempt_id FROM execution_log_catalog WHERE attempt_id>$after ORDER BY attempt_id LIMIT 200;";
                command.Parameters.AddWithValue("$after", after);
                using var rows = command.ExecuteReader();
                while (rows.Read())
                {
                    var text = rows.GetString(0); var id = Guid.ParseExact(text, "D");
                    if (id == Guid.Empty || id.ToString("D") != text) throw LogCatalogRows.Invalid();
                    page.Add(id);
                }
            }
            if (page.Count == 0) break;
            foreach (var id in page)
            {
                var receipt = AttemptReceiptStore.ReadInside(connection, transaction, id) ?? throw LogCatalogRows.Invalid();
                LogCatalogRows.RequireSaved(connection, transaction, original.CaptureCatalog(receipt));
            }
            after = page[^1].ToString("D");
        }
        using var orphan = connection.CreateCommand(); orphan.Transaction = transaction;
        orphan.CommandText = "SELECT EXISTS(SELECT 1 FROM execution_log_entries e LEFT JOIN execution_log_catalog c ON c.attempt_id=e.attempt_id WHERE c.attempt_id IS NULL);";
        if ((long)orphan.ExecuteScalar()! != 0) throw LogCatalogRows.Invalid();
        transaction.Commit();
    }
}
