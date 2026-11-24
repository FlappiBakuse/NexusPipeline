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
        var expected = original.CaptureCatalog(receipt, page: query);
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
        using (var transaction = DatabaseConnections.BeginRead(connection))
        {
            LogCatalogRows.RequireSaved(connection, transaction, expected);
            transaction.Commit();
        }
        var start = query.AfterPosition + 1;
        var items = expected.Entries.Skip(start).Take(query.Limit)
            .Select(entry => entry.Item with { ContentAvailability = expected.ContentAvailability[entry.Item.Position] }).ToArray();
        return new(items, expected.Entries.Count > start + query.Limit ? items[^1].Position : null);
    }

}
