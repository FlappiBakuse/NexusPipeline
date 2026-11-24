namespace NexusPipeline.Core.Records;

public enum RunLogRole { Main, PreHook, PostHook }
public sealed record RunLogKey(Guid RunId, Guid AttemptId, string StepId, RunLogRole Role = RunLogRole.Main)
{
    public int? InvocationOrdinal { get; init; }
}
public sealed record RunLogPart(int Index, long ObservedOffset, string Source, int? FileSourceId, long? FileOffset,
    long SizeBytes, string Sha256)
{
    public RecordContentAvailability ContentAvailability { get; init; }
}
public sealed record RunLogDescription(RunLogKey Key, string SnapshotSha256, long ObservedBytes, long RecordedBytes,
    long DiscardedBytes, bool IsComplete, IReadOnlyList<RunLogPart> Parts)
{
    public RecordContentAvailability ContentAvailability { get; init; }
}
public sealed record RunLogSlice(long ObservedOffset, string Source, int? FileSourceId, byte[] Bytes, long? FileOffset = null);
public sealed record RunLogTail(RunLogDescription Description, IReadOnlyList<RunLogSlice> Slices, bool PrefixOmitted);

public interface IRunLogReader
{
    RunLogDescription Describe(RunLogKey key);
    RunLogTail ReadTail(RunLogKey key, int maximumBytes = 65536);
    Stream OpenPart(RunLogKey key, string snapshotSha256, int partIndex);
    Stream OpenOriginal(RunLogKey key, string snapshotSha256);
}

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
