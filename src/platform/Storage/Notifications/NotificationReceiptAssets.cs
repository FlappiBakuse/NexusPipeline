using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal static class NotificationReceiptAssets
{
    internal static RecordAsset? ReadPublished(SqliteConnection connection, SqliteTransaction transaction, Guid runId)
    {
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT a.digest,b.size_bytes,b.relative_path FROM records.record_assets a JOIN content.blobs b ON b.digest=a.digest " +
            "WHERE a.run_id=$0 AND a.kind=0 ORDER BY a.digest LIMIT 1;", runId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        var asset = new RecordAsset(runId, row.GetString(0), row.GetInt64(1), RecordAssetKind.Screenshot);
        asset.Validate();
        if (row.GetString(2) != $"blobs/content/{asset.Sha256}.bin") throw NotificationReceiptCodec.Invalid();
        return asset;
    }
}
