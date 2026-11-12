using System.Runtime.Versioning;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal static class ImageCatalogInventory
{
    internal static void VerifyCatalog(StateDatabase database)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM execution_image_entries e LEFT JOIN execution_image_catalog c " +
            "ON c.attempt_id=e.attempt_id WHERE c.attempt_id IS NULL) OR EXISTS(SELECT 1 FROM execution_image_catalog c " +
            "LEFT JOIN attempt_receipts a ON a.attempt_id=c.attempt_id LEFT JOIN operation_journals j ON j.journal_id=a.journal_id " +
            "WHERE j.operation_kind IS NULL OR j.operation_kind<>'runAttempt' OR j.phase<>'committed') OR EXISTS(SELECT 1 FROM execution_image_asset_catalog c " +
            "LEFT JOIN run_records r ON r.run_id=c.run_id LEFT JOIN attempt_receipts a ON a.attempt_id=c.final_attempt_id " +
            "LEFT JOIN operation_journals j ON j.journal_id=a.journal_id WHERE r.run_id IS NULL OR r.attempt_id<>c.final_attempt_id " +
            "OR a.run_id<>c.run_id OR j.operation_kind IS NULL OR j.operation_kind<>'runAttempt');";
        if ((long)command.ExecuteScalar()! != 0) throw ImageCatalogRows.Invalid();
    }

    internal static void VerifyAttempt(RuntimeInstance instance, StateDatabase database, ContractReader contracts, AttemptWriterState state)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM execution_image_catalog WHERE attempt_id=$attempt), " +
            "EXISTS(SELECT 1 FROM execution_image_asset_catalog WHERE final_attempt_id=$attempt);";
        command.Parameters.AddWithValue("$attempt", state.Attempt.AttemptId);
        bool catalog, assets;
        using (var row = command.ExecuteReader())
        {
            if (!row.Read()) throw ImageCatalogRows.Invalid();
            catalog = row.GetInt64(0) != 0; assets = row.GetInt64(1) != 0;
        }
        if (!catalog && !assets) return;
        var receipt = AttemptReceiptStore.ReadInside(connection, transaction, Guid.Parse(state.Attempt.AttemptId)) ?? throw ImageCatalogRows.Invalid();
        var original = new ImageCatalogCapture(instance, database, contracts).ReadValidated(receipt, state);
        if (catalog) ImageCatalogRows.RequireSaved(connection, transaction, original);
        if (assets)
        {
            var family = new ImageRecordAssets(instance, database, contracts);
            family.Capture(receipt, original);
            family.RequirePublishedInside(connection, transaction, receipt);
        }
        transaction.Commit();
    }
}
