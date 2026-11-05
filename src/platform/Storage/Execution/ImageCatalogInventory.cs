using System.Runtime.Versioning;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal static class ImageCatalogInventory
{
    internal static void VerifyReadOnly(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw ImageCatalogRows.Invalid();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var original = new ImageCatalogCapture(instance, database, contracts); var after = "";
        while (true)
        {
            var page = new List<Guid>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT attempt_id FROM execution_image_catalog WHERE attempt_id>$after ORDER BY attempt_id LIMIT 64;";
                command.Parameters.AddWithValue("$after", after);
                using var rows = command.ExecuteReader();
                while (rows.Read())
                {
                    var text = rows.GetString(0); var id = Guid.ParseExact(text, "D");
                    if (id == Guid.Empty || id.ToString("D") != text) throw ImageCatalogRows.Invalid();
                    page.Add(id);
                }
            }
            if (page.Count == 0) break;
            foreach (var id in page)
            {
                var receipt = AttemptReceiptStore.ReadInside(connection, transaction, id) ?? throw ImageCatalogRows.Invalid();
                ImageCatalogRows.RequireSaved(connection, transaction, original.Read(receipt));
            }
            after = page[^1].ToString("D");
        }
        using var orphan = connection.CreateCommand(); orphan.Transaction = transaction;
        orphan.CommandText = "SELECT EXISTS(SELECT 1 FROM execution_image_entries e LEFT JOIN execution_image_catalog c " +
            "ON c.attempt_id=e.attempt_id WHERE c.attempt_id IS NULL);";
        if ((long)orphan.ExecuteScalar()! != 0) throw ImageCatalogRows.Invalid();
        transaction.Commit();
        VerifyAssets(instance, database, contracts);
    }

    private static void VerifyAssets(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var after = "";
        while (true)
        {
            var ids = new List<(string Run, Guid Attempt)>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT c.run_id,c.final_attempt_id FROM execution_image_asset_catalog c " +
                    "WHERE c.run_id>$after ORDER BY c.run_id LIMIT 64;";
                command.Parameters.AddWithValue("$after", after);
                using var rows = command.ExecuteReader();
                while (rows.Read()) ids.Add((rows.GetString(0), Guid.ParseExact(rows.GetString(1), "D")));
            }
            if (ids.Count == 0) break;
            foreach (var id in ids)
            {
                var receipt = AttemptReceiptStore.ReadInside(connection, transaction, id.Attempt) ?? throw ImageCatalogRows.Invalid();
                if (id.Run != receipt.Facts.RunId.ToString("D")) throw ImageCatalogRows.Invalid();
                var assets = new ImageRecordAssets(instance, database, contracts); assets.Capture(receipt);
                assets.RequirePublishedInside(connection, transaction, receipt);
            }
            after = ids[^1].Run;
        }
        transaction.Commit();
    }
}
