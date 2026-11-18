using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Records;

internal static class RetiredAssetReferences
{
    internal static HashSet<BlobReference> ReadReferenced(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, IReadOnlySet<BlobReference> content)
    {
        var referenced = new HashSet<BlobReference>();
        if (content.Count == 0) return referenced;
        var digests = content.GroupBy(item => item.Sha256).ToDictionary(group => group.Key, group => group.ToArray());
        string afterDigest = "", afterRun = ""; var afterKind = -1;
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true,
            domains: DatabaseDomain.Runtime | DatabaseDomain.Records | DatabaseDomain.Content);
        while (true)
        {
            var rows = new List<(string Digest, Guid Run, int Kind)>();
            using (var command = connection.CreateCommand())
            {
                var parameters = digests.Keys.Select((digest, index) =>
                {
                    var name = "$digest" + index; command.Parameters.AddWithValue(name, digest); return name;
                }).ToArray();
                command.CommandText = "SELECT a.digest,a.run_id,a.kind,b.size_bytes,b.relative_path FROM records.record_assets a " +
                    "LEFT JOIN content.blobs b USING(digest) WHERE a.digest IN (" + string.Join(',', parameters) + ") " +
                    "AND (a.digest,a.run_id,a.kind)>($afterDigest,$afterRun,$afterKind) ORDER BY a.digest,a.run_id,a.kind LIMIT 128;";
                command.Parameters.AddWithValue("$afterDigest", afterDigest);
                command.Parameters.AddWithValue("$afterRun", afterRun);
                command.Parameters.AddWithValue("$afterKind", afterKind);
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        if (reader.IsDBNull(3) || reader.IsDBNull(4) || reader.GetInt32(2) is < 0 or > 2 ||
                            digests[reader.GetString(0)].Any(item => item.SizeBytes != reader.GetInt64(3) || item.RelativePath != reader.GetString(4)))
                            throw RecordRetirementCodec.Invalid();
                        rows.Add((reader.GetString(0), Guid.ParseExact(reader.GetString(1), "D"), reader.GetInt32(2)));
                    }
            }
            foreach (var row in rows)
                foreach (var item in digests[row.Digest])
                    if (row.Kind != 0 || VerifiedImageRetirement.ForAsset(instance, database, contracts, row.Run, item) is null)
                        referenced.Add(item);
            if (rows.Count < 128) return referenced;
            afterDigest = rows[^1].Digest; afterRun = rows[^1].Run.ToString("D"); afterKind = rows[^1].Kind;
        }
    }
}
