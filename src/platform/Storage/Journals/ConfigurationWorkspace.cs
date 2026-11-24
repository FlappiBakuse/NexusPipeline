using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed class ConfigurationWorkspaceCodec(ContractReader contracts)
{
    public JsonElement Encode(PreparedConfigurationExchange prepared, string planDigest)
    {
        var wire = new ConfigurationWorkspace
        {
            Format = "nexus.configuration.workspace", Version = 1,
            InstanceId = prepared.Plan.InstanceId.ToString("D"), JournalId = prepared.Plan.JournalId.ToString("D"),
            PlanDigest = planDigest, WorkPath = prepared.WorkPath,
            WorkIdentity = ToWire(prepared.WorkIdentity),
            CandidateObjects = prepared.CandidateObjects.Select(item => new ConfigurationWorkspaceCandidateObjectsItem
                { RelativePath = item.RelativePath, Identity = ToWire(item.Identity) }).ToArray()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(wire);
        using var document = contracts.Read(bytes, "configuration-workspace.schema.json");
        var canonical = document.RootElement.Deserialize<ConfigurationWorkspace>() ?? throw new JsonException("null_contract");
        Bind(canonical, prepared.Plan, planDigest, requireLivePath: true);
        return document.RootElement.Clone();
    }

    public PreparedConfigurationExchange Decode(JsonElement identity, FrozenConfigurationExchange plan, string expectedPlanDigest)
        => Decode(identity, plan, expectedPlanDigest, requireLivePath: true);

    internal PreparedConfigurationExchange Decode(JsonElement identity, FrozenConfigurationExchange plan, string expectedPlanDigest, bool requireLivePath)
    {
        var wire = contracts.Read<ConfigurationWorkspace>(JsonSerializer.SerializeToUtf8Bytes(identity), "configuration-workspace.schema.json");
        return Bind(wire, plan, expectedPlanDigest, requireLivePath);
    }

    private static PreparedConfigurationExchange Bind(ConfigurationWorkspace wire, FrozenConfigurationExchange plan,
        string expectedPlanDigest, bool requireLivePath)
    {
        var expectedPath = Path.Combine(Path.GetDirectoryName(plan.TargetPath)!, ".nexus-" + plan.JournalId.ToString("D"));
        if (wire.InstanceId != plan.InstanceId.ToString("D") || wire.JournalId != plan.JournalId.ToString("D") ||
            wire.PlanDigest != expectedPlanDigest || wire.WorkPath != expectedPath ||
            wire.WorkIdentity.VolumeSerial != plan.ParentIdentity.VolumeSerial)
            throw new IOException("configuration_workspace_identity_unbound");
        var objects = wire.CandidateObjects.Select(item => new PhysicalTreeObject(item.RelativePath, FromWire(item.Identity))).ToArray();
        var originalIdentities = plan.Before.OriginalObjects.Select(item => item.Identity).ToHashSet();
        var candidatePaths = plan.Candidate.Snapshot.Entries.Select(item => item.RelativePath).ToHashSet(StringComparer.Ordinal);
        var workIdentity = FromWire(wire.WorkIdentity);
        if (objects.Length != plan.Candidate.Snapshot.Entries.Count || objects.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != objects.Length ||
            objects.Select(item => item.Identity).Distinct().Count() != objects.Length ||
            objects.Any(item => item.Identity == workIdentity || originalIdentities.Contains(item.Identity) ||
                item.Identity.VolumeSerial != plan.ParentIdentity.VolumeSerial || !candidatePaths.Contains(item.RelativePath)))
            throw new IOException("configuration_workspace_inventory_incomplete");
        if (requireLivePath) PhysicalPathPolicy.NormalizeWithoutLinks(wire.WorkPath);
        return new(plan, wire.WorkPath, workIdentity, Array.AsReadOnly(objects));
    }

    private static FileTreePhysicalIdentity ToWire(PhysicalObjectIdentity identity) => new()
        { VolumeSerial = identity.VolumeSerial, FileId = identity.FileId };
    private static PhysicalObjectIdentity FromWire(FileTreePhysicalIdentity identity) => new(identity.VolumeSerial, identity.FileId);
}

internal sealed record ConfigurationRestorationProof(string ObjectDigest)
{
    internal const string Format = "nexus.configuration.restoration";
    internal const string Step = "prepare-reconstruction";
    internal const string Intent = "configuration_reconstruction_started";

    internal static string Digest(IReadOnlyList<PhysicalTreeObject> objects) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(objects)));

    internal JsonObject Participant(Guid journalId, string planDigest, string resourceKey) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "file", ["resourceKey"] = resourceKey,
        ["identity"] = new JsonObject
        {
            ["format"] = Format, ["version"] = 1, ["journalId"] = journalId.ToString("D"),
            ["planDigest"] = planDigest, ["objectDigest"] = ObjectDigest
        }
    };

    internal static ConfigurationRestorationProof Read(JsonElement participant, JsonElement workspace, FrozenConfigurationExchange plan, JournalDocument physical)
    {
        var identity = participant.GetProperty("identity");
        if (physical.Phase is not (JournalPhase.Reverting or JournalPhase.Reverted or JournalPhase.Committed or JournalPhase.Quarantined) ||
            participant.GetProperty("kind").GetString() != "file" ||
            participant.GetProperty("resourceKey").GetString() != workspace.GetProperty("resourceKey").GetString() ||
            plan.Before.Snapshot.Shape == FileObjectKind.Missing || identity.EnumerateObject().Count() != 5 ||
            !identity.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var formatVersion) || formatVersion != 1 ||
            !HasString(identity, "journalId", plan.JournalId.ToString("D")) || !HasString(identity, "planDigest", physical.PlanDigest) ||
            !identity.TryGetProperty("objectDigest", out var digest) || digest.ValueKind != JsonValueKind.String ||
            digest.GetString() is not { Length: 64 } value || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new JsonException("configuration_reconstruction_proof_unbound");
        return new(value);
    }

    private static bool HasString(JsonElement value, string name, string expected) =>
        value.TryGetProperty(name, out var actual) && actual.ValueKind == JsonValueKind.String && actual.GetString() == expected;
}
