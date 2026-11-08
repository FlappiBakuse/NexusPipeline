using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

public sealed class ImageCatalogProjection(RuntimeInstance instance, StateDatabase database,
    ContractReader contracts, InstanceLease lease) : IAttemptTerminalObserver
{
    public void OnCommitted(AttemptReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt); lease.VerifyHeld(instance);
        if (!StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath, Path.Combine(instance.RuntimeRoot, "state", "nexus.db")))
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
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT a.attempt_id FROM attempt_receipts a LEFT JOIN execution_image_catalog c ON c.attempt_id=a.attempt_id " +
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
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT r.run_id,r.attempt_id FROM run_records r " +
                "JOIN attempt_receipts a ON a.attempt_id=r.attempt_id AND a.run_id=r.run_id " +
                "JOIN execution_start_sources s ON s.operation_id=a.operation_id " +
                "LEFT JOIN execution_image_asset_catalog c ON c.run_id=r.run_id " +
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
