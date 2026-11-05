using System.Collections.Immutable;
using System.Security.Cryptography;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ExecutionLogInvocation(int Ordinal, StepEvidenceBinding Binding, BlobReference Raw);
internal sealed record ExecutionLogAggregate(string Format, int Version, RunLogKey Key, ImmutableArray<ExecutionLogInvocation> Invocations);
internal sealed record ExecutionLogContent(BlobReference Reference, long ObservedBytes, long DiscardedBytes,
    bool IsComplete, ImmutableArray<RawOutputPart> Parts);

internal static class ExecutionLogAggregation
{
    internal static ExecutionLogContent Create(ImmutableBlobStore blobs, RunLogKey key,
        IReadOnlyList<(int Ordinal, BlobReference Reference, RawOutputManifest Raw)> sources, bool materialize)
    {
        if (sources.Count is < 1 or > 99) throw Invalid();
        if (sources.Count == 1)
        {
            var source = sources[0];
            return new(source.Reference, source.Raw.ObservedBytes, source.Raw.DiscardedBytes, source.Raw.IsComplete, source.Raw.Parts);
        }
        var aggregate = new ExecutionLogAggregate("nexus.execution.log-invocations", 1, key,
            sources.Select(source => new ExecutionLogInvocation(source.Ordinal, source.Raw.Binding, source.Reference)).ToImmutableArray());
        var bytes = BoundedJson.Serialize(aggregate, StepResultCodec.Options);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var reference = new BlobReference($"blobs/plans/{digest}.json", bytes.LongLength, digest);
        if (materialize)
        {
            if (blobs.PersistPlan(bytes) != reference) throw Invalid();
        }
        else using (var input = blobs.OpenVerified(reference)) { }
        long observed = 0, discarded = 0;
        var parts = ImmutableArray.CreateBuilder<RawOutputPart>();
        foreach (var source in sources)
        {
            if (!source.Raw.IsFinal || source.Raw.Binding.RunId != key.RunId || source.Raw.Binding.AttemptId != key.AttemptId ||
                source.Raw.Binding.StepId != key.StepId) throw Invalid();
            parts.AddRange(source.Raw.Parts.Select(part => part with { Offset = checked(part.Offset + observed) }));
            observed = checked(observed + source.Raw.ObservedBytes); discarded = checked(discarded + source.Raw.DiscardedBytes);
        }
        if (parts.Count > RawOutputCodec.MaximumParts || parts.Sum(part => part.Content.SizeBytes) > RawOutputCodec.MaximumRecordedBytes)
            throw Invalid();
        return new(reference, observed, discarded, sources.All(source => source.Raw.IsComplete), parts.ToImmutable());
    }

    private static IOException Invalid() => new("record_log_invocations_unbound");
}
