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
