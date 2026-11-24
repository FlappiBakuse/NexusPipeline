using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Serialization;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record JudgementInvocationArchive(string Format, int Version, JudgementWorkerIdentity Identity, long Generation,
    bool IsFinal, string InputSha256, long ProbeEvidenceAllowance, long SizeBytes, string Sha256, ImmutableArray<BlobReference> Parts)
{
    private const string ArchiveFormat = "nexus.execution.judgement-invocation-archive";
    private const int MaximumBytes = JudgementWorkerProtocol.MaximumInvocationBytes;
    private static readonly JsonSerializerOptions Options = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static BlobReference Write(ImmutableBlobStore blobs, JudgementInvocationFrame frame, Action<BlobReference> hold)
    {
        var invocation = frame.Invocation;
        var bytes = frame.Bytes;
        var parts = JudgementFrameParts.Write(blobs, bytes, MaximumBytes, hold);
        var archive = new JudgementInvocationArchive(ArchiveFormat, 1, invocation.Identity, invocation.Generation, invocation.IsFinal,
            invocation.InputSha256, invocation.ProbeEvidenceAllowance, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes.Span)), parts);
        var reference = blobs.PersistPlan(BoundedJson.Serialize(archive, Options)); hold(reference); return reference;
    }

    internal static JudgementInvocationArchive Read(ImmutableBlobStore blobs, BlobReference reference)
    {
        var bytes = JudgementEvidenceCodec.ReadBytes(blobs, reference);
        using var parsed = StrictJson.Parse(bytes);
        var value = parsed.RootElement.Deserialize<JudgementInvocationArchive>(Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(parsed.RootElement, JsonSerializer.SerializeToElement(value, Options)) ||
            value.Format != ArchiveFormat || value.Version != 1 || value.Identity is null || value.Generation is < 1 or > 4095 ||
            !FileTreePolicy.Digest(value.InputSha256) || value.ProbeEvidenceAllowance < JudgementProbeProtocol.FaultReserveBytes ||
            value.ProbeEvidenceAllowance > JudgementProbeProtocol.MaximumEvidenceBytes) throw Invalid();
        JudgementWorkerProtocol.RequireIdentity(value.Identity);
        JudgementFrameParts.Require(value.SizeBytes, value.Sha256, value.Parts, MaximumBytes);
        return value;
    }

    internal static JudgementWorkerInvocation Decode(ImmutableBlobStore blobs, BlobReference reference)
    {
        var value = Read(blobs, reference);
        var bytes = JudgementFrameParts.Read(blobs, value.SizeBytes, value.Sha256, value.Parts, MaximumBytes);
        var invocation = JudgementWorkerProtocol.Decode<JudgementWorkerInvocation>(bytes);
        JudgementWorkerProtocol.Require(invocation, value.Identity, value.Generation);
        if (invocation.IsFinal != value.IsFinal || invocation.InputSha256 != value.InputSha256 || invocation.ProbeEvidenceAllowance != value.ProbeEvidenceAllowance) throw Invalid();
        return invocation;
    }

    private static IOException Invalid() => new("execution_judgement_invocation_archive_unbound");
}

internal sealed record JudgementProbeArchive(string Format, int Version, JudgementWorkerIdentity Identity,
    long Generation, string InputSha256, int Ordinal, long SizeBytes, string Sha256, ImmutableArray<BlobReference> Parts)
{
    private const string ArchiveFormat = "nexus.execution.judgement-probe-archive";
    internal const int MaximumFrameBytes = JudgementProbeProtocol.MaximumFrameBytes;
    private static readonly JsonSerializerOptions Options = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static BlobReference Write(ImmutableBlobStore blobs, JudgementProbeFrame frame, Action<BlobReference> hold)
    {
        var bytes = frame.Bytes;
        var parts = JudgementFrameParts.Write(blobs, bytes, MaximumFrameBytes, hold);
        var request = frame.Exchange.Request;
        var value = new JudgementProbeArchive(ArchiveFormat, 1, request.Identity, request.Generation,
            request.InputSha256, request.Ordinal, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes.Span)), parts);
        var reference = blobs.PersistPlan(BoundedJson.Serialize(value, Options)); hold(reference); return reference;
    }

    internal static JudgementProbeArchive Read(ImmutableBlobStore blobs, BlobReference reference)
    {
        var bytes = JudgementEvidenceCodec.ReadBytes(blobs, reference);
        using var parsed = StrictJson.Parse(bytes);
        var value = parsed.RootElement.Deserialize<JudgementProbeArchive>(Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(parsed.RootElement, JsonSerializer.SerializeToElement(value, Options)) ||
            value.Format != ArchiveFormat || value.Version != 1 || value.Identity is null ||
            value.Generation is < 1 or > 4095 || !FileTreePolicy.Digest(value.InputSha256) ||
            value.Ordinal is < 1 or > JudgementProbeProtocol.MaximumCalls) throw Invalid();
        JudgementWorkerProtocol.RequireIdentity(value.Identity);
        JudgementFrameParts.Require(value.SizeBytes, value.Sha256, value.Parts, MaximumFrameBytes);
        return value;
    }

    internal static JudgementProbeExchange Decode(ImmutableBlobStore blobs, BlobReference reference)
    {
        var value = Read(blobs, reference);
        var bytes = JudgementFrameParts.Read(blobs, value.SizeBytes, value.Sha256, value.Parts, MaximumFrameBytes);
        var exchange = JudgementWorkerProtocol.Decode<JudgementProbeExchange>(bytes);
        var request = exchange.Request;
        if (request.Identity != value.Identity || request.Generation != value.Generation || request.InputSha256 != value.InputSha256 ||
            request.Ordinal != value.Ordinal) throw Invalid();
        JudgementProbeProtocol.Require(request, value.Identity, value.Generation, value.InputSha256, value.Ordinal);
        JudgementProbeProtocol.Require(exchange.Reply, request);
        return exchange;
    }

    private static IOException Invalid() => new("execution_judgement_probe_archive_unbound");
}
