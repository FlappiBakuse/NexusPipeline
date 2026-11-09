using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

public sealed class LogCatalogReader(RuntimeInstance instance, StateDatabase database, ContractReader contracts) : IRunLogCatalog
{
    public LogCatalogPage ReadPage(LogCatalogQuery query)
    {
        LogCatalogPolicy.Validate(query);
        var receipt = new AttemptReceiptStore(database).Read(query.AttemptId) ?? throw LogCatalogRows.Invalid();
        if (receipt.Facts.RunId != query.RunId) throw LogCatalogRows.Invalid();
        var original = new ExecutionLogReader(instance, database, contracts);
        var expected = original.CaptureCatalog(receipt);
        var selected = new List<LogCatalogEntry>();
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            if (AttemptReceiptStore.ReadInside(connection, transaction, query.AttemptId) != receipt) throw LogCatalogRows.Invalid();
            LogCatalogRows.RequireSaved(connection, transaction, expected);
            using var command = LogCatalogRows.PageCommand(connection, transaction, query);
            using (var rows = command.ExecuteReader())
                while (rows.Read()) selected.Add(LogCatalogRows.ReadEntry(rows, query.RunId, query.AttemptId));
            transaction.Commit();
        }
        var more = selected.Count > query.Limit;
        if (more) selected.RemoveAt(selected.Count - 1);
        return new(selected.Select(entry => entry.Item with { ContentAvailability = expected.ContentAvailability[entry.Item.Position] }).ToArray(), more ? selected[^1].Item.Position : null);
    }

}
