using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;

namespace NexusPipeline.Platform.Storage.Sqlite;

public sealed class AttemptHistoryReader(StateDatabase database) : IAttemptHistoryReader
{
    public AttemptHistoryPage ReadPage(AttemptHistoryQuery query)
    {
        AttemptHistoryPolicy.Validate(query);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT attempt_id,ordinal FROM attempt_receipts WHERE run_id=$run AND ordinal>$ordinal " +
            "AND NOT EXISTS(SELECT 1 FROM run_records WHERE run_id=$run AND history_retired_at_utc_ticks IS NOT NULL) ORDER BY ordinal LIMIT $limit;";
        command.Parameters.AddWithValue("$run", query.RunId.ToString("D"));
        command.Parameters.AddWithValue("$ordinal", query.AfterOrdinal);
        command.Parameters.AddWithValue("$limit", query.Limit + 1);
        var ids = new List<(Guid AttemptId, int Ordinal)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var text = reader.GetString(0); var ordinal = reader.GetInt32(1);
                if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || id.ToString("D") != text ||
                    ordinal is < 1 or > 99 || ordinal <= (ids.Count == 0 ? query.AfterOrdinal : ids[^1].Ordinal))
                    throw new IOException("attempt_history_metadata_unrecognized");
                ids.Add((id, ordinal));
            }
        var hasNext = ids.Count > query.Limit;
        if (hasNext) ids.RemoveAt(ids.Count - 1);
        var records = new List<AttemptReceipt>(ids.Count);
        foreach (var item in ids)
        {
            var receipt = AttemptReceiptStore.ReadInside(connection, transaction, item.AttemptId)
                ?? throw new IOException("attempt_history_metadata_unrecognized");
            if (receipt.Facts.RunId != query.RunId || receipt.Facts.Ordinal != item.Ordinal)
                throw new IOException("attempt_history_metadata_unrecognized");
            records.Add(receipt);
        }
        return new(records.AsReadOnly(), hasNext ? records[^1].Facts.Ordinal : null);
    }
}
