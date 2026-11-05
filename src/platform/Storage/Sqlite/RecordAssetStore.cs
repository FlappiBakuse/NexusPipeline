using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage.Records;

namespace NexusPipeline.Platform.Storage.Sqlite;

public sealed class RecordAssetStore(StateDatabase database, ImmutableBlobStore blobs) : IAssetContentStore
{
    internal void AttachVerified(RecordAsset asset)
    {
        asset.Validate();
        VerifyBinding();
        var reference = new BlobReference($"blobs/content/{asset.Sha256}.bin", asset.SizeBytes, asset.Sha256);
        using var held = blobs.OpenVerified(reference);
        database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO blobs VALUES($digest,$size,$path) ON CONFLICT(digest) DO NOTHING;";
            command.Parameters.AddWithValue("$digest", asset.Sha256);
            command.Parameters.AddWithValue("$size", asset.SizeBytes);
            command.Parameters.AddWithValue("$path", reference.RelativePath);
            command.ExecuteNonQuery();
            command.CommandText = "SELECT size_bytes,relative_path FROM blobs WHERE digest=$digest;";
            using (var existing = command.ExecuteReader())
                if (!existing.Read() || existing.GetInt64(0) != asset.SizeBytes || existing.GetString(1) != reference.RelativePath)
                    throw new IOException("record_blob_identity_conflict");
            command.CommandText = "INSERT INTO record_assets VALUES($run,$digest,$kind) ON CONFLICT(run_id,digest,kind) DO NOTHING;";
            command.Parameters.AddWithValue("$run", asset.RunId.ToString("D"));
            command.Parameters.AddWithValue("$kind", (int)asset.Kind);
            command.ExecuteNonQuery();
            return true;
        });
    }

    public RecordAssetPage ReadForRun(Guid runId, RecordAssetCursor? after = null, int limit = 50)
    {
        VerifyBinding(); ValidatePage(runId, after, limit);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = CreatePageQuery(connection, runId, after, limit);
        using var reader = command.ExecuteReader();
        var records = new List<RecordAsset>();
        while (reader.Read()) records.Add(new(runId, reader.GetString(0), reader.GetInt64(1), (RecordAssetKind)reader.GetInt32(2)));
        var hasNext = records.Count > limit;
        if (hasNext) records.RemoveAt(records.Count - 1);
        return new(records.AsReadOnly(), hasNext ? new(records[^1].Sha256, records[^1].Kind) : null);
    }

    internal IReadOnlyList<string> ExplainPage(Guid runId, RecordAssetCursor? after = null, int limit = 50)
    {
        VerifyBinding(); ValidatePage(runId, after, limit);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = CreatePageQuery(connection, runId, after, limit);
        command.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
        using var rows = command.ExecuteReader(); var result = new List<string>();
        while (rows.Read()) result.Add(rows.GetString(3));
        return result.AsReadOnly();
    }

    private static void ValidatePage(Guid runId, RecordAssetCursor? after, int limit)
    {
        if (runId == Guid.Empty || limit is < 1 or > 200 || after is not null &&
            (after.Sha256 is not { Length: 64 } || !Enum.IsDefined(after.Kind) ||
                after.Sha256.Any(character => character is not (>= 'a' and <= 'f' or >= '0' and <= '9'))))
            throw new ArgumentException("record_asset_query_invalid");
    }

    private static SqliteCommand CreatePageQuery(SqliteConnection connection, Guid runId, RecordAssetCursor? after, int limit)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT a.digest,b.size_bytes,a.kind FROM record_assets a JOIN blobs b ON a.digest=b.digest " +
            "WHERE a.run_id=$run AND (a.digest,a.kind)>($after,$kind) ORDER BY a.digest,a.kind LIMIT $limit;";
        command.Parameters.AddWithValue("$run", runId.ToString("D"));
        command.Parameters.AddWithValue("$after", after?.Sha256 ?? "");
        command.Parameters.AddWithValue("$kind", after is null ? -1 : (int)after.Kind);
        command.Parameters.AddWithValue("$limit", limit + 1); return command;
    }

    public Stream OpenVerified(RecordAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        asset.Validate();
        VerifyBinding();
        BlobReference reference;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT b.size_bytes,b.relative_path FROM record_assets a JOIN blobs b ON a.digest=b.digest " +
                "WHERE a.run_id=$run AND a.digest=$digest AND a.kind=$kind;";
            command.Parameters.AddWithValue("$run", asset.RunId.ToString("D"));
            command.Parameters.AddWithValue("$digest", asset.Sha256);
            command.Parameters.AddWithValue("$kind", (int)asset.Kind);
            using var row = command.ExecuteReader();
            if (!row.Read() || row.GetInt64(0) != asset.SizeBytes ||
                row.GetString(1) != $"blobs/content/{asset.Sha256}.bin")
                throw new IOException("record_asset_reference_unbound");
            reference = new(row.GetString(1), row.GetInt64(0), asset.Sha256);
        }
        if (asset.Kind == RecordAssetKind.Screenshot)
            VerifiedImageRetirement.ForAsset(blobs.Instance, database, new(), asset.RunId, reference)?.ThrowRetired();
        // The verified handle pins only this immutable file, leaving SQLite free while a subscriber reads slowly.
        return blobs.OpenVerified(reference);
    }

    private void VerifyBinding()
    {
        if (database.InstanceId != blobs.Instance.InstanceId || !string.Equals(database.DatabasePath,
            Path.Combine(blobs.Instance.RuntimeRoot, "state", "nexus.db"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("record_asset_store_instance_unbound");
    }
}
