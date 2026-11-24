using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

internal static class RawOutputReplay
{
    internal static bool Read(ImmutableBlobStore blobs, RawOutputManifest manifest, Action<string> observe, bool includePartial = false,
        Action<int, string?>? observeSource = null)
    {
        var continuous = manifest.DiscardedBytes == 0 && manifest.FailureCode is null;
        var decoder = new ExecutionOutputDecoder(manifest.FileLogBaseline is not null, observe, _ => continuous = false, observeSource);
        var ends = Ends(manifest);
        var nextEnd = 0;
        foreach (var part in manifest.Parts)
        {
            using var input = blobs.OpenVerified(part.Content); var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            var position = 0; var source = part.FileSourceId ?? (part.Source == Processes.ProcessOutputSource.StandardOutput ? -2 : -1);
            while (nextEnd < ends.Count && ends[nextEnd].Offset <= part.Offset + bytes.Length)
            {
                var end = ends[nextEnd++];
                var length = checked((int)Math.Clamp(end.Offset - part.Offset - position, 0, bytes.Length - position));
                if (continuous) decoder.Write(source, bytes.AsSpan(position, length)); position += length;
                if (continuous) decoder.Write(end.Source, [], true, end.Flush);
            }
            if (continuous) decoder.Write(source, bytes.AsSpan(position));
        }
        while (nextEnd < ends.Count)
        {
            var end = ends[nextEnd++]; if (continuous) decoder.Write(end.Source, [], true, end.Flush);
        }
        if (continuous && includePartial) decoder.FlushObservedTails();
        return continuous;
    }

    internal static List<(long Offset, long Order, int Source, bool Flush)> Ends(RawOutputManifest manifest)
    {
        var ends = new List<(long Offset, long Order, int Source, bool Flush)>();
        if (manifest.StandardOutputEofOffset is { } stdout) ends.Add((stdout, manifest.StandardOutputEndOrder!.Value, -2, true));
        if (manifest.StandardErrorEofOffset is { } stderr) ends.Add((stderr, manifest.StandardErrorEndOrder!.Value, -1, true));
        foreach (var source in manifest.FileSources)
            if (source.EndOffset is { } end) ends.Add((end, source.EndOrder!.Value, source.Id, source.FlushPartial));
        ends.Sort((left, right) => left.Offset != right.Offset ? left.Offset.CompareTo(right.Offset) : left.Order.CompareTo(right.Order));
        return ends;
    }
}

internal sealed class ExecutionOutputDecoder(bool files, Action<string> observe, Action<string> fail,
    Action<int, string?>? observeSource = null)
{
    private sealed class Source
    {
        internal readonly Decoder Decoder = new UTF8Encoding(false, true).GetDecoder();
        internal readonly StringBuilder Line = new();
        internal bool Invalid;
    }
    private readonly Dictionary<int, Source> _sources = [];
    private readonly char[] _characters = new char[8192];

    internal void Write(int id, ReadOnlySpan<byte> bytes, bool final = false, bool flushPartial = true)
    {
        if (files != (id >= 0) && observeSource is null) return;
        if (!_sources.TryGetValue(id, out var state)) _sources.Add(id, state = new());
        if (state.Invalid) return;
        if (final && !flushPartial) { _sources.Remove(id); return; }
        try
        {
            var position = 0; bool completed;
            do
            {
                state.Decoder.Convert(bytes[position..], _characters, final, out var used, out var count, out completed);
                position += used;
                foreach (var character in _characters.AsSpan(0, count))
                {
                    if (character == '\n') Finish(id, state);
                    else
                    {
                        if (state.Line.Length == 4096) { Reject(id, state, "execution_output_line_exceeded"); return; }
                        state.Line.Append(character);
                    }
                }
                if (!completed && used == 0 && count == 0) throw new IOException("execution_output_decoder_stalled");
            } while (!completed);
            if (final && state.Line.Length != 0) Finish(id, state);
        }
        catch (DecoderFallbackException) { Reject(id, state, "execution_output_encoding_invalid"); }
    }

    private void Reject(int id, Source state, string reason)
    {
        state.Invalid = true;
        if (files == (id >= 0)) fail(reason);
        else observeSource?.Invoke(id, null);
    }

    private void Finish(int id, Source state)
    {
        var text = state.Line.ToString(); state.Line.Clear();
        var line = text.EndsWith('\r') ? text[..^1] : text;
        if (files == (id >= 0)) observe(line);
        observeSource?.Invoke(id, line);
    }

    internal void FlushObservedTails()
    {
        foreach (var source in _sources.Keys.ToArray()) Write(source, [], final: true);
    }
}
