using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed class MaintenancePhysicalCodec(RuntimeInstance instance, ContractReader contracts)
{
    internal const string Format = "nexus.maintenance.payload-physical";
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    internal static void RequireCapacity(FrozenPayloadApply plan)
    {
        var identity = new { VolumeSerial = new string('f', 16), FileId = new string('f', 32) };
        var measurement = new
        {
            Format, Version = 1, plan.InstanceId, plan.OperationId, plan.JournalId,
            PlanDigest = new string('f', 64), Source = plan.MaintenanceSource,
            BeforeRevision = long.MaxValue, Stage = Sequence(plan).MaxBy(value => value.Length)!,
                        WorkspaceIdentity = identity,
            CandidateObjects = plan.Candidate.Snapshot.Entries.Select(item => new
            {
                item.RelativePath, Identity = identity
            }).ToArray()
        };
        try { _ = BoundedJson.Serialize(measurement, Options); }
        catch (JsonException exception) { throw new IOException("maintenance_payload_metadata_capacity", exception); }
    }

    internal BlobReference Persist(MaintenancePhysicalRecord value, FrozenPayloadApply plan)
    {
        RequireRecord(value, plan);
        return _blobs.PersistPlan(BoundedJson.Serialize(value, Options));
    }

    internal MaintenancePhysicalRecord Read(BlobReference reference, FrozenPayloadApply plan)
    {
        RequireReference(reference);
        using var input = _blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        using var strict = StrictJson.Parse(bytes);
        var value = JsonSerializer.Deserialize<MaintenancePhysicalRecord>(bytes, Options) ?? throw Invalid();
        RequireRecord(value, plan);
        if (!JsonElement.DeepEquals(strict.RootElement, JsonSerializer.SerializeToElement(value, Options))) throw Invalid();
        return value;
    }

    internal ImmutableArray<MaintenancePhysicalEntry> ReadHistory(IReadOnlyList<JournalDocument> history,
        FrozenPayloadApply plan, string planDigest)
    {
        var sequence = Sequence(plan);
        var saved = ImmutableArray.CreateBuilder<MaintenancePhysicalEntry>();
        foreach (var document in history)
        {
            var found = ReadIntents(document.Root, plan);
            if (found.Length > sequence.Length || found.Length < saved.Count || found.Length > saved.Count + 1) throw Invalid();
            for (var index = 0; index < saved.Count; index++)
                if (found[index] != saved[index].Intent) throw Invalid();
            for (var index = saved.Count; index < found.Length; index++)
            {
                var intent = found[index];
                if (document.Revision == 1 || intent.BeforeRevision != document.Revision - 1 ||
                    intent.Stage != sequence[index]) throw Invalid();
                var value = Read(intent.Artifact, plan);
                if (value.BeforeRevision != intent.BeforeRevision || value.Stage != intent.Stage ||
                    value.PlanDigest != planDigest) throw Invalid();
                if (saved.Count > 0) RequireSuccessor(saved[^1].Record, value);
                saved.Add(new(intent, value));
            }
            if (document.Phase == JournalPhase.Prepared && found.Length != 0 ||
                document.Phase is JournalPhase.Applying or JournalPhase.Applied && found.Length == 0 ||
                document.Phase == JournalPhase.Applied && found.Length != sequence.Length ||
                document.Phase == JournalPhase.Applying && found.Length == sequence.Length) throw Invalid();
        }
        return saved.ToImmutable();
    }

    internal static bool IsParticipant(JsonElement participant) =>
        participant.TryGetProperty("identity", out var identity) && identity.TryGetProperty("format", out var format) &&
        format.ValueKind == JsonValueKind.String && format.GetString() == Format;

    internal static JsonObject Participant(MaintenancePhysicalIntent intent) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "directory", ["resourceKey"] = "maintenance:host-payload",
        ["identity"] = JsonSerializer.SerializeToNode(intent, Options)
    };

    internal static ImmutableArray<BlobReference> ReadReferences(JsonElement root, FrozenPayloadApply plan) =>
        ReadIntents(root, plan).Select(item => item.Artifact).Distinct().ToImmutableArray();

    private static ImmutableArray<MaintenancePhysicalIntent> ReadIntents(JsonElement root, FrozenPayloadApply plan)
    {
        var result = ImmutableArray.CreateBuilder<MaintenancePhysicalIntent>();
        foreach (var participant in root.GetProperty("participants").EnumerateArray())
        {
            if (!IsParticipant(participant)) continue;
            var identity = participant.GetProperty("identity");
            var value = identity.Deserialize<MaintenancePhysicalIntent>(Options) ?? throw Invalid();
            if (!JsonElement.DeepEquals(identity, JsonSerializer.SerializeToElement(value, Options)) ||
                value.Format != Format || value.Version != 1 || value.BeforeRevision < 1 ||
                !Guid.TryParseExact(participant.GetProperty("participantId").GetString(), "D", out var participantId) ||
                participantId == Guid.Empty || participant.GetProperty("kind").GetString() != "directory" ||
                participant.GetProperty("resourceKey").GetString() != "maintenance:host-payload") throw Invalid();
            RequireReference(value.Artifact); result.Add(value);
            if (result.Count > 6) throw Invalid();
        }
        return result.ToImmutable();
    }

    private void RequireRecord(MaintenancePhysicalRecord value, FrozenPayloadApply plan)
    {
        if (value is null || value.Format != Format || value.Version != 1 ||
            value.InstanceId != instance.InstanceId || plan.InstanceId != instance.InstanceId ||
            value.OperationId != plan.OperationId || value.JournalId != plan.JournalId ||
            plan.Kind != JournalKind.UpdateApply || value.Source != plan.MaintenanceSource ||
            value.BeforeRevision < 1 || !Digest(value.PlanDigest) ||
            !Sequence(plan).Contains(value.Stage, StringComparer.Ordinal) || value.CandidateObjects.IsDefault) throw Invalid();
        RequireReference(value.Source);
        if (value.Stage == "old-family-stopped" ? value.WorkspaceIdentity is not null : value.WorkspaceIdentity is null) throw Invalid();
        if (value.WorkspaceIdentity is { } workspace)
        {
            RequireIdentity(workspace);
            if (workspace.VolumeSerial != plan.ParentIdentity.VolumeSerial) throw Invalid();
        }
        var hasCandidate = value.Stage is "candidate-materialized" or "candidate-installed" ||
            value.Stage is "original-preservation-started" or "original-preserved" && plan.Action != PayloadAction.Remove;
        if (!hasCandidate)
        {
            if (!value.CandidateObjects.IsEmpty) throw Invalid();
            return;
        }
        var expected = plan.Candidate.Snapshot.Entries;
        if (value.CandidateObjects.Length != expected.Count || value.CandidateObjects.Any(item => item is null) ||
            !value.CandidateObjects.Select(item => item.RelativePath).SequenceEqual(expected.Select(item => item.RelativePath)) ||
            value.CandidateObjects.Select(item => item.Identity).Distinct().Count() != value.CandidateObjects.Length) throw Invalid();
        foreach (var item in value.CandidateObjects)
        {
            RequireIdentity(item.Identity);
            if (item.Identity.VolumeSerial != plan.ParentIdentity.VolumeSerial || item.Identity == plan.ParentIdentity ||
                item.Identity == value.WorkspaceIdentity) throw Invalid();
        }
    }

    private static void RequireSuccessor(MaintenancePhysicalRecord previous, MaintenancePhysicalRecord next)
    {
        if (previous.WorkspaceIdentity is not null && previous.WorkspaceIdentity != next.WorkspaceIdentity ||
            !previous.CandidateObjects.IsEmpty && !previous.CandidateObjects.SequenceEqual(next.CandidateObjects)) throw Invalid();
    }

    internal static ImmutableArray<string> Sequence(FrozenPayloadApply plan)
    {
        if (plan.Kind != JournalKind.UpdateApply || plan.Action != PayloadAction.Upgrade ||
            plan.Before.Snapshot.Shape != FileObjectKind.Directory || plan.MaintenanceSource is null ||
            plan.ExtensionSource is not null) throw Invalid();
        return ["old-family-stopped", "workspace-created", "candidate-materialized", "original-preservation-started",
            "original-preserved", "candidate-installed"];
    }

    private static void RequireIdentity(PhysicalObjectIdentity value)
    {
        if (value is null || !Hex(value.VolumeSerial, 16) || !Hex(value.FileId, 32)) throw Invalid();
    }
    private static bool Hex(string value, int length) => value is not null && value.Length == length &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Digest(string value) => Hex(value, 64);
    private static void RequireReference(BlobReference value)
    {
        if (value is null || value.SizeBytes is < 1 or > 2097152 || !Digest(value.Sha256) ||
            value.RelativePath != "blobs/plans/" + value.Sha256 + ".json") throw Invalid();
    }
    private static IOException Invalid() => new("maintenance_payload_physical_unbound");
}
