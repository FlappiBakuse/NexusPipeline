using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record MuMuDiscoveryStart(string Format, int Version, Guid InstanceId, Guid DiscoveryId,
    string Endpoint, ExecutionPlanInterpreterImage Manager, ProcessIdentity Creator, string JobName, ProcessIdentity Root);

internal sealed record MuMuDiscoveryDocument(string Format, int Version, Guid InstanceId, Guid DiscoveryId,
    string Endpoint, ExecutionPlanInterpreterImage Manager, BlobReference Start, ProcessIdentity Creator, string JobName,
    int SessionId, ProcessIdentity Root, ImmutableArray<ProcessIdentity> Family, WorkerJobAccounting Accounting,
    int? ExitCode, bool StartReturned, bool OutputComplete, long? MappingIndex, ImmutableArray<TargetCommandPart> Parts, long ElapsedMilliseconds);

internal static class MuMuDiscoveryEvidence
{
    internal const string Format = "nexus.execution.mumu-discovery";
    internal static readonly JsonSerializerOptions Options = StepResultCodec.Options;

    internal static MuMuDiscoveryDocument Read(ImmutableBlobStore blobs, BlobReference artifact)
    {
        StepEvidenceCodec.Plan(artifact);
        using var input = blobs.OpenVerified(artifact);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        using var parsed = StrictJson.Parse(bytes);
        var value = parsed.RootElement.Deserialize<MuMuDiscoveryDocument>(Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(parsed.RootElement, JsonSerializer.SerializeToElement(value, Options)) ||
            value.Format != Format || value.Version != 1 || value.InstanceId == Guid.Empty || value.DiscoveryId == Guid.Empty ||
            value.Endpoint != ExecutionAndroidCommands.Endpoint(value.Endpoint) || !new Uri("http://" + value.Endpoint).IsLoopback ||
            value.Root is null || value.Creator is null || value.Manager is null || value.Family.IsDefaultOrEmpty || value.Family.Any(item => item is null) ||
            value.Accounting is null || value.Accounting.ActiveProcesses != 0 || value.Accounting.TotalProcesses is < 1 or > 4096 ||
            value.Family.Length != value.Accounting.TotalProcesses ||
            value.Family.DistinctBy(item => (item.ProcessId, item.CreatedAtUtc)).Count() != value.Family.Length ||
            !value.Family.Contains(value.Root) || value.ElapsedMilliseconds < 0 || value.Parts.IsDefault ||
            value.MappingIndex is < 0 or > int.MaxValue || value.MappingIndex is not null && (!value.StartReturned || !value.OutputComplete || value.ExitCode != 0) ||
            value.Parts.Length > 4 || value.Parts.Any(part => part is null || part.Content is null || !Enum.IsDefined(part.Source) || part.Content.SizeBytes is < 1 or > 1048576) ||
            value.Parts.Sum(part => part.Content.SizeBytes) > TargetCommandOutput.MaximumBytes) throw Invalid();
        StepEvidenceCodec.Plan(value.Start);
        foreach (var part in value.Parts)
            if (!FileTreePolicy.Digest(part.Content.Sha256) || part.Content.RelativePath != $"blobs/content/{part.Content.Sha256}.bin") throw Invalid();
        foreach (var identity in value.Family.Append(value.Creator)) RequireIdentity(identity);
        if (value.Manager is null || value.Root.ImagePath != value.Manager.Path || value.Root.ImageSha256 != value.Manager.Sha256 ||
            value.JobName is null || value.JobName.Length != 50 || !value.JobName.StartsWith("Local\\NexusWorker-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(value.JobName[18..], "N", out var jobId) || jobId == Guid.Empty || value.JobName[18..] != jobId.ToString("N") || value.SessionId < 0 ||
            !double.IsFinite(value.Accounting.CpuSeconds) || value.Accounting.CpuSeconds < 0) throw Invalid();
        using var started = blobs.OpenVerified(value.Start);
        var startBytes = new byte[checked((int)started.Length)]; started.ReadExactly(startBytes);
        using var startJson = StrictJson.Parse(startBytes);
        var start = new MuMuDiscoveryStart(Format + "-start", 1, value.InstanceId, value.DiscoveryId, value.Endpoint,
            value.Manager, value.Creator, value.JobName, value.Root);
        if (!JsonElement.DeepEquals(startJson.RootElement, JsonSerializer.SerializeToElement(start, Options))) throw Invalid();
        return value;
    }

    [SupportedOSPlatform("windows")]
    internal static MuMuDiscoveryDocument RequireMapping(ImmutableBlobStore blobs, BlobReference artifact, string endpoint,
        ExecutionPlanInterpreterImage manager, long index)
    {
        var value = Read(blobs, artifact);
        if (!value.StartReturned || !value.OutputComplete || value.ExitCode != 0 || value.Endpoint != endpoint ||
            !SameImage(value.Manager, manager) || index < 0 || value.MappingIndex != index ||
            MuMuInfoParser.Parse(Stdout(blobs, value), endpoint) != index.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new IOException("execution_mumu_mapping_unbound");
        return value;
    }

    internal static IReadOnlyList<BlobReference> ReadReferences(ImmutableBlobStore blobs, BlobReference artifact)
    {
        var value = Read(blobs, artifact);
        return new[] { artifact, value.Start }.Concat(value.Parts.Select(part => part.Content)).Distinct().ToArray();
    }

    internal static string Stdout(ImmutableBlobStore blobs, MuMuDiscoveryDocument value)
    {
        using var bytes = new MemoryStream();
        foreach (var part in value.Parts.Where(part => part.Source == ProcessOutputSource.StandardOutput))
        { using var input = blobs.OpenVerified(part.Content); input.CopyTo(bytes); }
        return new UTF8Encoding(false, true).GetString(bytes.ToArray());
    }

    internal static bool SameImage(ExecutionPlanInterpreterImage first, ExecutionPlanInterpreterImage second) =>
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(first, Options), JsonSerializer.SerializeToElement(second, Options));
    private static void RequireIdentity(ProcessIdentity value)
    {
        if (value is null || value.ProcessId < 1 || value.CreatedAtUtc.Kind != DateTimeKind.Utc || value.CreatedAtUtc.Ticks <= 0 ||
            !Path.IsPathFullyQualified(value.ImagePath) || !FileTreePolicy.Digest(value.ImageSha256) ||
            string.IsNullOrWhiteSpace(value.UserSid)) throw Invalid();
    }

    private static IOException Invalid() => new("execution_mumu_discovery_evidence_invalid");
}
