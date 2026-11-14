using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class ImageRecordAssets(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    private readonly List<ImageCatalogSnapshot> _family = [];

    internal void Capture(AttemptReceipt final, ImageCatalogSnapshot? current = null)
    {
        if (_family.Count != 0) throw ImageCatalogRows.Invalid();
        IReadOnlyList<AttemptReceipt> receipts;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            ExecutionRecordWitness.RequireFinalInside(connection, transaction, final);
            receipts = ReadFamily(connection, transaction, final);
            transaction.Commit();
        }
        var reader = new ImageCatalogCapture(instance, database, contracts);
        foreach (var receipt in receipts) _family.Add(current?.Receipt == receipt ? current : reader.Read(receipt));
    }

    internal void PublishInside(SqliteConnection connection, SqliteTransaction transaction, AttemptReceipt final)
    {
        RequireCurrentFamily(connection, transaction, final);
        if (HasMarker(connection, transaction, final.Facts.RunId))
        { RequirePublishedInside(connection, transaction, final); return; }
        foreach (var source in _family)
        {
            ImageCatalogRows.PublishInside(connection, transaction, source);
            foreach (var entry in source.Entries.Where(entry => entry.Item.Outcome == RunImageOutcome.Captured))
            {
                var png = entry.Png ?? throw ImageCatalogRows.Invalid();
                using var blob = connection.CreateCommand(); blob.Transaction = transaction;
                blob.CommandText = "INSERT INTO content.blobs VALUES($digest,$size,$path) ON CONFLICT(digest) DO NOTHING;";
                blob.Parameters.AddWithValue("$digest", png.Sha256); blob.Parameters.AddWithValue("$size", png.SizeBytes);
                blob.Parameters.AddWithValue("$path", png.RelativePath); blob.ExecuteNonQuery();
                blob.CommandText = "SELECT size_bytes,relative_path FROM content.blobs WHERE digest=$digest;";
                using (var row = blob.ExecuteReader())
                    if (!row.Read() || row.GetInt64(0) != png.SizeBytes || row.GetString(1) != png.RelativePath)
                        throw ImageCatalogRows.Invalid();
                blob.CommandText = "INSERT INTO records.record_assets VALUES($run,$digest,$kind) ON CONFLICT(run_id,digest,kind) DO NOTHING;";
                blob.Parameters.AddWithValue("$run", final.Facts.RunId.ToString("D"));
                blob.Parameters.AddWithValue("$kind", (int)RecordAssetKind.Screenshot); blob.ExecuteNonQuery();
            }
        }
        using var marker = connection.CreateCommand(); marker.Transaction = transaction;
        marker.CommandText = "INSERT INTO records.execution_image_asset_catalog VALUES($run,$attempt,$family,$count);";
        marker.Parameters.AddWithValue("$run", final.Facts.RunId.ToString("D"));
        marker.Parameters.AddWithValue("$attempt", final.Facts.AttemptId.ToString("D"));
        marker.Parameters.AddWithValue("$family", FamilyDigest(final));
        marker.Parameters.AddWithValue("$count", Pngs().Select(png => png.Sha256).Distinct().Count());
        marker.ExecuteNonQuery();
        RequirePublishedInside(connection, transaction, final);
    }

    internal void RequirePublishedInside(SqliteConnection connection, SqliteTransaction transaction, AttemptReceipt final)
    {
        RequireCurrentFamily(connection, transaction, final);
        using var marker = connection.CreateCommand(); marker.Transaction = transaction;
        marker.CommandText = "SELECT final_attempt_id,family_sha256,image_count FROM records.execution_image_asset_catalog WHERE run_id=$run;";
        marker.Parameters.AddWithValue("$run", final.Facts.RunId.ToString("D"));
        using (var row = marker.ExecuteReader())
            if (!row.Read() || row.GetString(0) != final.Facts.AttemptId.ToString("D") || row.GetString(1) != FamilyDigest(final) ||
                row.GetInt32(2) != Pngs().Select(png => png.Sha256).Distinct().Count()) throw ImageCatalogRows.Invalid();
        foreach (var source in _family) ImageCatalogRows.RequireSaved(connection, transaction, source);
        foreach (var png in Pngs().Distinct())
        {
            using var asset = connection.CreateCommand(); asset.Transaction = transaction;
            asset.CommandText = "SELECT EXISTS(SELECT 1 FROM records.record_assets a JOIN content.blobs b ON b.digest=a.digest " +
                "WHERE a.run_id=$run AND a.kind=0 AND a.digest=$digest AND b.size_bytes=$size AND b.relative_path=$path);";
            asset.Parameters.AddWithValue("$run", final.Facts.RunId.ToString("D"));
            asset.Parameters.AddWithValue("$digest", png.Sha256); asset.Parameters.AddWithValue("$size", png.SizeBytes);
            asset.Parameters.AddWithValue("$path", png.RelativePath);
            if ((long)asset.ExecuteScalar()! != 1) throw ImageCatalogRows.Invalid();
        }
    }

    private IEnumerable<BlobReference> Pngs() => _family.SelectMany(value => value.Entries)
        .Where(entry => entry.Item.Outcome == RunImageOutcome.Captured).Select(entry => entry.Png ?? throw ImageCatalogRows.Invalid());

    private string FamilyDigest(AttemptReceipt final)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(final);
        foreach (var source in _family)
        {
            Append(source.Receipt); Append(source.TerminalRevision); Append(source.TerminalSha256); Append(source.Entries.Count);
            foreach (var entry in source.Entries)
            {
                var item = entry.Item;
                Append(new
                {
                    Item = new { item.Position, item.Key, item.Outcome, item.ReasonCode, item.Width, item.Height,
                        item.SourceSha256, item.RootSha256, item.ResultSha256, item.ImageSha256, item.SizeBytes },
                    entry.Source, entry.Root, entry.Result, entry.Png
                });
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Append<T>(T value)
        {
            var bytes = BoundedJson.Serialize(value, StepResultCodec.Options);
            var size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, bytes.Length);
            hash.AppendData(size); hash.AppendData(bytes);
        }
    }

    private void RequireCurrentFamily(SqliteConnection connection, SqliteTransaction transaction, AttemptReceipt final)
    {
        ExecutionRecordWitness.RequireFinalInside(connection, transaction, final);
        if (!ReadFamily(connection, transaction, final).SequenceEqual(_family.Select(value => value.Receipt)))
            throw ImageCatalogRows.Invalid();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT attempt_id,attempt_ordinal,engine_outcome,business_outcome,lifecycle_outcome,recovery_outcome,reason_code " +
            "FROM records.run_records WHERE run_id=$run;";
        command.Parameters.AddWithValue("$run", final.Facts.RunId.ToString("D"));
        using var row = command.ExecuteReader(); var facts = final.Facts;
        if (!row.Read() || row.GetString(0) != facts.AttemptId.ToString("D") || row.GetInt32(1) != facts.Ordinal ||
            row.GetInt32(2) != (int)facts.Engine || row.GetInt32(3) != (int)facts.Business ||
            row.GetInt32(4) != (int)facts.Lifecycle || row.GetInt32(5) != (int)facts.Recovery ||
            row.GetString(6) != facts.ReasonCode) throw ImageCatalogRows.Invalid();
    }

    private static bool HasMarker(SqliteConnection connection, SqliteTransaction transaction, Guid run)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM records.execution_image_asset_catalog WHERE run_id=$run;";
        command.Parameters.AddWithValue("$run", run.ToString("D")); return command.ExecuteScalar() is not null;
    }

    private static IReadOnlyList<AttemptReceipt> ReadFamily(SqliteConnection connection, SqliteTransaction transaction, AttemptReceipt final)
    {
        var ids = new List<Guid>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT attempt_id FROM records.attempt_receipts WHERE run_id=$run ORDER BY ordinal LIMIT 100;";
            command.Parameters.AddWithValue("$run", final.Facts.RunId.ToString("D"));
            using var rows = command.ExecuteReader();
            while (rows.Read()) ids.Add(Guid.ParseExact(rows.GetString(0), "D"));
        }
        if (ids.Count is < 1 or > 99 || ids.Count != final.Facts.Ordinal || ids[^1] != final.Facts.AttemptId)
            throw ImageCatalogRows.Invalid();
        var receipts = ids.Select(id => AttemptReceiptStore.ReadInside(connection, transaction, id) ?? throw ImageCatalogRows.Invalid()).ToArray();
        if (receipts.Where((receipt, index) => receipt.Facts.RunId != final.Facts.RunId || receipt.Facts.Ordinal != index + 1).Any() ||
            receipts[^1] != final) throw ImageCatalogRows.Invalid();
        return receipts;
    }
}
