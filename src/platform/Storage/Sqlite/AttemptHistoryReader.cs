using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;

namespace NexusPipeline.Platform.Storage.Sqlite;

public sealed class AttemptHistoryReader(StateDatabase database) : IAttemptHistoryReader
{
    public AttemptHistoryPage ReadPage(AttemptHistoryQuery query)
    {
        AttemptHistoryPolicy.Validate(query);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records);
        using var transaction = DatabaseConnections.BeginRead(connection);
        return ReadInside(connection, transaction, query);
    }

    internal static AttemptHistoryPage ReadInside(SqliteConnection connection, SqliteTransaction transaction, AttemptHistoryQuery query)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT attempt_id," + AttemptReceiptStore.ReceiptColumns + " FROM records.attempt_receipts WHERE run_id=$run AND ordinal>$ordinal " +
            "AND NOT EXISTS(SELECT 1 FROM records.run_records WHERE run_id=$run AND history_retired_at_utc_ticks IS NOT NULL) ORDER BY ordinal LIMIT $limit;";
        command.Parameters.AddWithValue("$run", query.RunId.ToString("D"));
        command.Parameters.AddWithValue("$ordinal", query.AfterOrdinal);
        command.Parameters.AddWithValue("$limit", query.Limit + 1);
        var rows = new List<(Guid AttemptId, AttemptReceiptStore.StoredReceipt Receipt)>();
        var previousOrdinal = query.AfterOrdinal;
        var hasNext = false;
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var text = reader.GetString(0); var ordinal = reader.GetInt32(2);
                if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || id.ToString("D") != text ||
                    ordinal is < 1 or > 99 || ordinal <= previousOrdinal)
                    throw new IOException("attempt_history_metadata_unrecognized");
                if (rows.Count == query.Limit) { hasNext = true; break; }
                rows.Add((id, AttemptReceiptStore.ReadColumns(reader, 1, ordinal)));
                previousOrdinal = ordinal;
            }
        // All keys, including the lookahead, are checked before binding the returned receipt bodies.
        var records = new List<AttemptReceipt>(rows.Count);
        foreach (var item in rows)
        {
            var receipt = item.Receipt.Bind(item.AttemptId);
            if (receipt.Facts.RunId != query.RunId)
                throw new IOException("attempt_history_metadata_unrecognized");
            records.Add(receipt);
        }
        return new(records.AsReadOnly(), hasNext ? records[^1].Facts.Ordinal : null);
    }
}
