using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record RawOutputPart(long Offset, ProcessOutputSource Source, BlobReference Content)
{
    public int? FileSourceId { get; init; }
    public long? FileOffset { get; init; }
}
internal sealed record FileLogEpoch(int Id, int DeclarationIndex, string Path, PhysicalObjectIdentity Identity,
    PhysicalObjectIdentity ParentIdentity, DateTimeOffset SelectedAt, long StartOffset, string Reason,
    long? EndOffset, long? EndOrder, bool FlushPartial);
internal sealed record RawOutputManifest(string Format, int Version, StepEvidenceBinding Binding, Guid SegmentId,
    bool IsFinal, long ObservedBytes, long DiscardedBytes, long? StandardOutputEofOffset, long? StandardOutputEndOrder,
    long? StandardErrorEofOffset, long? StandardErrorEndOrder, string? FailureCode, ImmutableArray<RawOutputPart> Parts)
{
    public BlobReference? FileLogBaseline { get; init; }
    public ImmutableArray<FileLogEpoch> FileSources { get; init; } = [];
    public bool FilesComplete { get; init; } = true;
    internal bool IsComplete => IsFinal && StandardOutputEofOffset is not null && StandardErrorEofOffset is not null &&
        FilesComplete && DiscardedBytes == 0 && FailureCode is null;
}

internal static class RawOutputCodec
{
    internal const string Format = "nexus.execution.raw-output";
    internal const int MaximumParts = 4096;
    internal const int MaximumFileEpochs = 256;
    internal const int MaximumRecordedBytes = 64 * 1024 * 1024;
    internal const long MaximumObservedBytes = 1_099_511_627_776;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<ProcessOutputSource>(JsonNamingPolicy.CamelCase, false) }
    };

    internal static BlobReference Write(ImmutableBlobStore blobs, RawOutputManifest value)
    {
        Validate(value, value.Binding);
        return blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(value, Options));
    }

    internal static RawOutputManifest Read(ImmutableBlobStore blobs, StepEvidenceBinding expected, BlobReference artifact)
    {
        if (artifact.RelativePath != $"blobs/plans/{artifact.Sha256}.json" || artifact.SizeBytes is < 1 or > StrictJson.MaximumBytes) throw Invalid();
        using var stream = blobs.OpenVerified(artifact);
        var value = BlobReadStream.ReadEvidence(stream, () =>
        {
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            using var document = StrictJson.Parse(bytes);
            var result = document.RootElement.Deserialize<RawOutputManifest>(Options) ?? throw Invalid();
            if (!JsonElement.DeepEquals(document.RootElement, JsonSerializer.SerializeToElement(result, Options))) throw Invalid();
            Validate(result, expected); return result;
        });
        if (value.Binding != expected) throw Invalid();
        return value;
    }

    private static void Validate(RawOutputManifest value, StepEvidenceBinding expected)
    {
        if (value.Format != Format || value.Version != 1 || value.Binding != expected || value.SegmentId == Guid.Empty ||
            value.ObservedBytes is < 0 or > MaximumObservedBytes || value.DiscardedBytes < 0 || value.DiscardedBytes > value.ObservedBytes ||
            value.Parts.IsDefault || value.Parts.Length > MaximumParts || value.FileSources.IsDefault || value.FileSources.Length > MaximumFileEpochs ||
            value.FileLogBaseline is null && (!value.FileSources.IsEmpty || !value.FilesComplete) ||
            value.IsFinal && !value.FilesComplete && value.FailureCode is null ||
            !End(value.StandardOutputEofOffset, value.StandardOutputEndOrder) || !End(value.StandardErrorEofOffset, value.StandardErrorEndOrder) ||
            value.StandardOutputEndOrder is not null && value.StandardOutputEndOrder == value.StandardErrorEndOrder ||
            (value.DiscardedBytes > 0 || value.IsFinal && (value.StandardOutputEofOffset is null || value.StandardErrorEofOffset is null)) && value.FailureCode is null ||
            value.FailureCode is not null && (value.FailureCode.Length is < 1 or > 80 || value.FailureCode.Any(character =>
                character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')))) throw Invalid();
        if (value.FileLogBaseline is { } baseline) StepEvidenceCodec.Plan(baseline);
        var fileEnds = new Dictionary<int, long>();
        var endOrders = new HashSet<long>();
        if (value.StandardOutputEndOrder is { } outOrder) endOrders.Add(outOrder);
        if (value.StandardErrorEndOrder is { } errorOrder) endOrders.Add(errorOrder);
        foreach (var source in value.FileSources)
        {
            if (source is null || source.Id != fileEnds.Count || source.DeclarationIndex is < 0 or > 15 || source.StartOffset is < 0 or > MaximumObservedBytes ||
                source.SelectedAt.Offset != TimeSpan.Zero || !Path.IsPathFullyQualified(source.Path) || source.Path != Path.GetFullPath(source.Path) ||
                source.Reason is not ("baseline" or "created" or "rewritten") || !End(source.EndOffset, source.EndOrder) ||
                value.IsFinal && value.FilesComplete && source.EndOffset is null ||
                source.EndOrder is { } fileEnd && !endOrders.Add(fileEnd)) throw Invalid();
            LogBaselineCodec.RequireIdentity(source.Identity);
            LogBaselineCodec.RequireIdentity(source.ParentIdentity);
            fileEnds.Add(source.Id, source.StartOffset);
        }
        long end = 0, recorded = 0;
        foreach (var part in value.Parts)
        {
            if (part is null || !Enum.IsDefined(part.Source) || part.Content is null || part.Offset < end ||
                part.Content.SizeBytes is < 1 or > 65536 || !Digest(part.Content.Sha256) ||
                part.Content.RelativePath != $"blobs/content/{part.Content.Sha256}.bin" ||
                part.Offset > value.ObservedBytes - part.Content.SizeBytes || (part.FileSourceId is null) != (part.FileOffset is null)) throw Invalid();
            var eof = part.Source == ProcessOutputSource.StandardOutput ? value.StandardOutputEofOffset : value.StandardErrorEofOffset;
            if (part.FileSourceId is { } id)
            {
                if (part.Source != ProcessOutputSource.StandardOutput || !fileEnds.TryGetValue(id, out var position) || part.FileOffset < position ||
                    part.FileOffset > MaximumObservedBytes - part.Content.SizeBytes ||
                    value.DiscardedBytes == 0 && part.FileOffset != position) throw Invalid();
                fileEnds[id] = part.FileOffset!.Value + part.Content.SizeBytes; eof = value.FileSources[id].EndOffset;
            }
            end = part.Offset + part.Content.SizeBytes; recorded += part.Content.SizeBytes;
            if (eof is { } offset && end > offset || recorded > MaximumRecordedBytes) throw Invalid();
        }
        if (recorded != value.ObservedBytes - value.DiscardedBytes) throw Invalid();
        return;

        bool End(long? offset, long? order) => (offset is null) == (order is null) &&
            (offset is null || offset >= 0 && offset <= value.ObservedBytes && order >= 1 && order <= value.ObservedBytes + 2 + MaximumFileEpochs * 2);
    }

    private static bool Digest(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static JsonException Invalid() => new("execution_raw_output_manifest_unrecognized");
}
