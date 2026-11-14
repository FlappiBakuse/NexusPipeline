using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Records;

internal static class RecordRetirementInventory
{
    internal static void VerifyReadOnly(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
    {
        var after = "";
        while (true)
        {
            var page = new List<(Guid RunId, BlobReference Reference, Guid OperationId)>();
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
            using (var command = RecordRetirementRows.Command(connection, null,
                "SELECT operation_id,run_id,relative_path,size_bytes,sha256 FROM records.record_content_retirements " +
                "WHERE operation_id>$0 ORDER BY operation_id LIMIT 64;", after))
            using (var rows = command.ExecuteReader())
                while (rows.Read()) page.Add((Guid.ParseExact(rows.GetString(1), "D"),
                    new(rows.GetString(2), rows.GetInt64(3), rows.GetString(4)), Guid.ParseExact(rows.GetString(0), "D")));
            if (page.Count == 0) break;
            foreach (var item in page)
            {
                var family = new RetirementFamilyReader(instance, database, contracts).CaptureMetadata(item.RunId);
                var document = VerifiedRecordRetirement.ReadProof(instance, database, contracts, family, item.Reference);
                if (document.OperationId != item.OperationId) throw RecordRetirementCodec.Invalid();
            }
            after = page[^1].OperationId.ToString("D");
        }
        using var check = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var query = RecordRetirementRows.Command(check, null,
            "SELECT EXISTS(SELECT 1 FROM main.operations o LEFT JOIN records.record_content_retirements r USING(operation_id) " +
            "WHERE o.operation_kind='records.content.retire' AND r.operation_id IS NULL) " +
            "OR EXISTS(SELECT 1 FROM records.record_retired_contents c LEFT JOIN records.record_content_retirements r USING(operation_id) WHERE r.operation_id IS NULL);");
        if ((long)query.ExecuteScalar()! != 0) throw RecordRetirementCodec.Invalid();
    }
}
