using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed record BatchResultItem(ConfigurationAddress Address, long Revision, bool Changed);
internal sealed record BatchResultMetadata(string? SourceRequestDigest, IReadOnlyList<BatchResultItem> Items);
internal sealed record BatchSourceRequest(ConfigurationAddress Address, long ExpectedRevision,
    long ExpectedBindingRevision, ConfigurationContractStamp Contract, string Digest);

internal static class BatchResultRows
{
    internal static BatchResultMetadata Read(SqliteConnection connection, SqliteTransaction transaction, Guid operationId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.CommandText = "SELECT resource_count,source_request_digest FROM configuration_batch_results WHERE operation_id=$operation;";
        int count;
        string? digest;
        using (var row = command.ExecuteReader())
        {
            if (!row.Read()) throw new IOException("configuration_batch_result_missing");
            count = row.GetInt32(0); digest = row.IsDBNull(1) ? null : row.GetString(1);
            if (count is < 1 or > 32 || digest is not null && !FileTreePolicy.Digest(digest))
                throw new IOException("configuration_batch_result_unrecognized");
        }
        command.CommandText = "SELECT ordinal,binding_id,resource_id,revision,has_changes FROM configuration_batch_items " +
            "WHERE operation_id=$operation ORDER BY ordinal LIMIT 33;";
        var items = new List<BatchResultItem>();
        using (var row = command.ExecuteReader())
        {
            while (row.Read())
            {
                if (row.GetInt32(0) != items.Count || row.GetInt64(3) is < 1 or > 9007199254740991 || row.GetInt64(4) is not (0 or 1))
                    throw new IOException("configuration_batch_result_unrecognized");
                items.Add(new(new(Guid.Parse(row.GetString(1)), row.GetString(2)), row.GetInt64(3), row.GetBoolean(4)));
            }
        }
        if (items.Count != count || items.Select(item => item.Address.BindingId).Distinct().Count() != 1 ||
            !items.Select(item => item.Address.ResourceId).SequenceEqual(items.Select(item => item.Address.ResourceId).Distinct()
                .Order(StringComparer.Ordinal)))
            throw new IOException("configuration_batch_result_unrecognized");
        return new(digest, items.AsReadOnly());
    }
}
