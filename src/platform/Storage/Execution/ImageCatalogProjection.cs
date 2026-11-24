using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Storage.Execution;

public sealed class ImageCatalogProjection(RuntimeInstance instance, StateDatabase database,
    ContractReader contracts, InstanceLease lease) : IAttemptTerminalObserver
{
    public void OnCommitted(AttemptReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt); lease.VerifyHeld(instance);
        if (!StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath, Path.Combine(instance.RuntimeRoot, "state", "runtime.db")))
            throw ImageCatalogRows.Invalid();
        lease.PublishExecution(() =>
        {
            var expected = new ImageCatalogCapture(instance, database, contracts).Read(receipt);
            database.Write((connection, transaction) =>
            { ImageCatalogRows.PublishInside(connection, transaction, expected); return true; });
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
            command.CommandText = "SELECT a.attempt_id FROM records.attempt_receipts a LEFT JOIN records.execution_image_catalog c ON c.attempt_id=a.attempt_id " +
                "WHERE a.attempt_id>$after AND c.attempt_id IS NULL ORDER BY a.attempt_id LIMIT $limit;";
            command.Parameters.AddWithValue("$after", after?.ToString("D") ?? ""); command.Parameters.AddWithValue("$limit", maximum);
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                var text = rows.GetString(0); var id = Guid.ParseExact(text, "D");
                if (id == Guid.Empty || id.ToString("D") != text) throw ImageCatalogRows.Invalid();
                ids.Add(id);
            }
        }
        Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { kind = "startup.projection-page",
            projection = "images", inspected = ids.Count, maximum, after }));
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnCommitted(new AttemptReceiptStore(database).Read(id) ?? throw ImageCatalogRows.Invalid());
        }
        return ids.Count == maximum ? ids[^1] : null;
    }

    [SupportedOSPlatform("windows")]
    public Guid? ReconcileAssetsPage(Guid? after = null, int maximum = 64, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lease.VerifyHeld(instance);
        if (after == Guid.Empty || maximum is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(maximum));
        var ids = new List<(Guid Run, Guid Attempt)>();
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT r.run_id,r.attempt_id FROM records.run_records r " +
                "JOIN records.attempt_receipts a ON a.attempt_id=r.attempt_id AND a.run_id=r.run_id " +
                "JOIN main.execution_start_sources s ON s.operation_id=a.operation_id " +
                "LEFT JOIN records.execution_image_asset_catalog c ON c.run_id=r.run_id " +
                "WHERE r.run_id>$after AND c.run_id IS NULL ORDER BY r.run_id LIMIT $limit;";
            command.Parameters.AddWithValue("$after", after?.ToString("D") ?? ""); command.Parameters.AddWithValue("$limit", maximum);
            using var rows = command.ExecuteReader();
            while (rows.Read()) ids.Add((Guid.ParseExact(rows.GetString(0), "D"), Guid.ParseExact(rows.GetString(1), "D")));
        }
        Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { kind = "startup.projection-page",
            projection = "image-assets", inspected = ids.Count, maximum, after }));
        foreach (var item in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receipt = new AttemptReceiptStore(database).Read(item.Attempt) ?? throw ImageCatalogRows.Invalid();
            if (receipt.Facts.RunId != item.Run) throw ImageCatalogRows.Invalid();
            lease.PublishExecution(() =>
            {
                var assets = new ImageRecordAssets(instance, database, contracts); assets.Capture(receipt);
                database.Write((connection, transaction) => { assets.PublishInside(connection, transaction, receipt); return true; });
            });
        }
        return ids.Count == maximum ? ids[^1].Run : null;
    }
}

[SupportedOSPlatform("windows")]
internal static class ImageCatalogInventory
{
    internal static void VerifyCatalog(StateDatabase database)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM records.execution_image_entries e LEFT JOIN records.execution_image_catalog c " +
            "ON c.attempt_id=e.attempt_id WHERE c.attempt_id IS NULL) OR EXISTS(SELECT 1 FROM records.execution_image_catalog c " +
            "LEFT JOIN records.attempt_receipts a ON a.attempt_id=c.attempt_id LEFT JOIN main.operation_journals j ON j.journal_id=a.journal_id " +
            "WHERE j.operation_kind IS NULL OR j.operation_kind<>'runAttempt' OR j.phase<>'committed') OR EXISTS(SELECT 1 FROM records.execution_image_asset_catalog c " +
            "LEFT JOIN records.run_records r ON r.run_id=c.run_id LEFT JOIN records.attempt_receipts a ON a.attempt_id=c.final_attempt_id " +
            "LEFT JOIN main.operation_journals j ON j.journal_id=a.journal_id WHERE r.run_id IS NULL OR r.attempt_id<>c.final_attempt_id " +
            "OR a.run_id<>c.run_id OR j.operation_kind IS NULL OR j.operation_kind<>'runAttempt');";
        if ((long)command.ExecuteScalar()! != 0) throw ImageCatalogRows.Invalid();
    }

    internal static void VerifyAttempt(RuntimeInstance instance, StateDatabase database, ContractReader contracts, AttemptWriterState state)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var transaction = DatabaseConnections.BeginRead(connection);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM records.execution_image_catalog WHERE attempt_id=$attempt), " +
            "EXISTS(SELECT 1 FROM records.execution_image_asset_catalog WHERE final_attempt_id=$attempt);";
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
