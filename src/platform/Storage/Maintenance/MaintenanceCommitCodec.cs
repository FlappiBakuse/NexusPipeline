using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed record MaintenanceCommitIntent(string Format, int Version, long BeforeRevision, BlobReference Receipt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BlobReference? Installation { get; init; }
}
internal sealed record MaintenanceCommitEntry(MaintenanceCommitIntent Intent, Guid HandoffId,
    Guid StartupGeneration, ProcessIdentity Host, string JobName, PhysicalObjectIdentity InstalledRoot);

[SupportedOSPlatform("windows")]
internal sealed class MaintenanceCommitCodec(RuntimeInstance instance, ContractReader contracts)
{
    internal const string Format = "nexus.maintenance.startup-commit";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    internal MaintenanceCommitEntry? ReadHistory(IReadOnlyList<JournalDocument> history,
        FrozenPayloadApply plan, BlobReference planReference, HostUpdateSource source,
        IReadOnlyList<MaintenancePhysicalEntry> physical)
    {
        MaintenanceCommitEntry? result = null;
        foreach (var document in history)
        {
            var participants = document.Root.GetProperty("participants").EnumerateArray().Where(IsParticipant).ToArray();
            if (participants.Length == 0)
            {
                if (result is not null) throw Invalid();
                continue;
            }
            if (result is not null || participants.Length != 1 || document.Phase != JournalPhase.Committed ||
                physical.Count != MaintenancePhysicalCodec.Sequence(plan).Length ||
                document.Revision != physical[^1].Intent.BeforeRevision + 2) throw Invalid();
            var participant = participants[0];
            var identity = participant.GetProperty("identity");
            var intent = identity.Deserialize<MaintenanceCommitIntent>(Options) ?? throw Invalid();
            if (!JsonElement.DeepEquals(identity, JsonSerializer.SerializeToElement(intent, Options)) ||
                intent.Format != Format || intent.Version != 1 || intent.BeforeRevision != document.Revision - 1 ||
                participant.GetProperty("kind").GetString() != "process" ||
                participant.GetProperty("resourceKey").GetString() != "maintenance:host-payload" ||
                intent.Receipt.SizeBytes is < 1 or > StrictJson.MaximumBytes ||
                intent.Receipt.RelativePath != "blobs/plans/" + intent.Receipt.Sha256 + ".json") throw Invalid();
            result = ReadReceipt(intent, plan, planReference, source, physical[^1].Record);
            var receipts = document.Root.GetProperty("receipts");
            if (receipts.GetArrayLength() != 1 || receipts[0].GetProperty("stepId").GetString() != "maintenance-startup" ||
                receipts[0].GetProperty("effect").GetString() != "completed" ||
                receipts[0].GetProperty("detailCode").GetString() != "candidate_started") throw Invalid();
        }
        return result;
    }

    private MaintenanceCommitEntry ReadReceipt(MaintenanceCommitIntent intent, FrozenPayloadApply plan,
        BlobReference planReference, HostUpdateSource source, MaintenancePhysicalRecord installed)
    {
        using var input = new ImmutableBlobStore(instance, contracts).OpenVerified(intent.Receipt);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        using var document = contracts.Read(bytes, "update-receipt.schema.json");
        var root = document.RootElement;
        if (root.GetProperty("stage").GetString() != "candidate-prepared" ||
            root.GetProperty("detailCode").GetString() != "candidate_startup_prepared" ||
            root.GetProperty("instanceId").GetGuid() != instance.InstanceId ||
            root.GetProperty("operationId").GetGuid() != plan.OperationId || root.GetProperty("journalId").GetGuid() != plan.JournalId ||
            root.GetProperty("planSha256").GetString() != planReference.Sha256 ||
            root.GetProperty("payloadManifestSha256").GetString() != source.PayloadManifest.Sha256 ||
            root.GetProperty("oldPayloadSha256").GetString() != StartupReceiptStore.TreeDigest(plan.Before.Snapshot) ||
            root.GetProperty("candidatePayloadSha256").GetString() != StartupReceiptStore.TreeDigest(plan.Candidate.Snapshot)) throw Invalid();
        var release = new UpdateSourceCodec(instance, contracts).RequireFrozenBinding(source, plan);
        var store = root.GetProperty("storeBinding"); var binding = release.StoreBinding;
        if (store.GetProperty("channel").GetString() != (binding.Channel == Core.Extensions.ExtensionStoreChannel.Stable ? "stable" : "develop") ||
            store.GetProperty("generationId").GetGuid() != binding.GenerationId || store.GetProperty("sequence").GetInt64() != binding.Sequence ||
            store.GetProperty("catalogUrl").GetString() != binding.CatalogUrl || store.GetProperty("catalogSha256").GetString() != binding.CatalogSha256 ||
            store.GetProperty("signatureUrl").GetString() != binding.SignatureUrl) throw Invalid();
        using var manifestInput = new ImmutableBlobStore(instance, contracts).OpenVerified(source.PayloadManifest);
        var manifestBytes = new byte[checked((int)manifestInput.Length)]; manifestInput.ReadExactly(manifestBytes);
        var manifest = new UpdateManifestCodec(contracts).Read(manifestBytes, release, requiredProfile: null);
        var host = root.GetProperty("process");
        var process = new ProcessIdentity(host.GetProperty("processId").GetInt32(), DateTime.FromFileTimeUtc(host.GetProperty("createdFileTime").GetInt64()),
            host.GetProperty("imagePath").GetString()!, host.GetProperty("imageSha256").GetString()!,
            host.GetProperty("userSid").GetString()!, host.GetProperty("isElevated").GetBoolean());
        var expected = manifest.Files.Single(file => file.RelativePath == manifest.HostEntry);
        if (!StringComparer.OrdinalIgnoreCase.Equals(process.ImagePath, PhysicalPathPolicy.NormalizeSyntax(Path.Combine(plan.TargetPath, manifest.HostEntry))) ||
            process.ImageSha256 != expected.Sha256) throw Invalid();
        var startup = root.GetProperty("startup"); var installedRoot = startup.GetProperty("installedRoot");
        var identity = new PhysicalObjectIdentity(installedRoot.GetProperty("volumeSerial").GetString()!, installedRoot.GetProperty("fileId").GetString()!);
        var handoff = startup.GetProperty("handoffId").GetGuid();
        var generation = startup.GetProperty("generationId").GetGuid();
        var job = startup.GetProperty("jobName").GetString()!;
        if (handoff == Guid.Empty || generation == Guid.Empty || root.GetProperty("correlationId").GetGuid() != handoff ||
            job.Length != 50 || !job.StartsWith("Local\\NexusWorker-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(job[18..], "N", out _) ||
            ApplicationTree.Installed(plan, installed.CandidateObjects).OriginalIdentity != identity)
            throw Invalid();
        var result = new MaintenanceCommitEntry(intent, handoff, generation, process, job, identity);
        new MaintenanceInstallation(instance, contracts).RequireCommit(source, plan, profile: null, result);
        return result;
    }

    internal static bool IsParticipant(JsonElement participant) =>
        participant.TryGetProperty("identity", out var identity) && identity.TryGetProperty("format", out var format) &&
        format.ValueKind == JsonValueKind.String && format.GetString() == Format;

    internal static JsonObject Participant(MaintenanceCommitIntent intent) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "process", ["resourceKey"] = "maintenance:host-payload",
        ["identity"] = JsonSerializer.SerializeToNode(intent, Options)
    };
    private static IOException Invalid() => new("maintenance_startup_commit_unbound");
}
