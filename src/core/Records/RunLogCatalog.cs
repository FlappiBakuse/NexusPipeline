namespace NexusPipeline.Core.Records;

public sealed record LogCatalogQuery(Guid RunId, Guid AttemptId, int AfterPosition = -1, int Limit = 50);
public sealed record LogCatalogItem(int Position, RunLogKey Key, string SnapshotSha256, long ObservedBytes,
    long RecordedBytes, long DiscardedBytes, bool IsComplete, int PartCount)
{
    public RecordContentAvailability ContentAvailability { get; init; }
}
public sealed record LogCatalogPage(IReadOnlyList<LogCatalogItem> Items, int? NextPosition);

public interface IRunLogCatalog
{
    LogCatalogPage ReadPage(LogCatalogQuery query);
}

public static class LogCatalogPolicy
{
    public static void Validate(LogCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.RunId == Guid.Empty || query.AttemptId == Guid.Empty || query.AfterPosition is < -1 or > 4095 ||
            query.Limit is < 1 or > 100) throw new ArgumentException("record_log_catalog_query_invalid");
    }
}
