using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Records;

internal static class RetiredAssetReferences
{
    internal static bool IsReferenced(RuntimeInstance instance, StateDatabase database, ContractReader contracts, BlobReference content)
    {
        string afterRun = ""; var afterKind = -1;
        while (true)
        {
            var rows = new List<(Guid Run, int Kind)>();
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
            using (var command = RecordRetirementRows.Command(connection, null,
                "SELECT a.run_id,a.kind,b.size_bytes,b.relative_path FROM records.record_assets a LEFT JOIN content.blobs b USING(digest) " +
                "WHERE a.digest=$0 AND (a.run_id,a.kind)>($1,$2) ORDER BY a.run_id,a.kind LIMIT 128;",
                content.Sha256, afterRun, afterKind))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    if (reader.IsDBNull(2) || reader.IsDBNull(3) || reader.GetInt64(2) != content.SizeBytes || reader.GetString(3) != content.RelativePath ||
                        reader.GetInt32(1) is < 0 or > 2) throw RecordRetirementCodec.Invalid();
                    rows.Add((Guid.ParseExact(reader.GetString(0), "D"), reader.GetInt32(1)));
                }
            foreach (var row in rows)
                if (row.Kind != 0 || VerifiedImageRetirement.ForAsset(instance, database, contracts, row.Run, content) is null)
                    return true;
            if (rows.Count < 128) return false;
            afterRun = rows[^1].Run.ToString("D"); afterKind = rows[^1].Kind;
        }
    }
}
