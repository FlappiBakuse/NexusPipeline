using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

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
        Decode(document.RootElement, prepared.Plan, planDigest);
        return document.RootElement.Clone();
    }

    public PreparedConfigurationExchange Decode(JsonElement identity, FrozenConfigurationExchange plan, string expectedPlanDigest)
    {
        var wire = contracts.Read<ConfigurationWorkspace>(JsonSerializer.SerializeToUtf8Bytes(identity), "configuration-workspace.schema.json");
        var expectedPath = Path.Combine(Path.GetDirectoryName(plan.TargetPath)!, ".nexus-" + plan.JournalId.ToString("D"));
        if (wire.InstanceId != plan.InstanceId.ToString("D") || wire.JournalId != plan.JournalId.ToString("D") ||
            wire.PlanDigest != expectedPlanDigest || wire.WorkPath != expectedPath ||
            wire.WorkIdentity.VolumeSerial != plan.ParentIdentity.VolumeSerial)
            throw new IOException("configuration_workspace_identity_unbound");
        var objects = wire.CandidateObjects.Select(item => new PhysicalTreeObject(item.RelativePath, FromWire(item.Identity))).ToArray();
        if (objects.Length != plan.Candidate.Snapshot.Entries.Count || objects.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != objects.Length ||
            objects.Select(item => item.Identity).Distinct().Count() != objects.Length ||
            objects.Any(item => item.Identity == FromWire(wire.WorkIdentity) || plan.Before.OriginalObjects.Any(original => original.Identity == item.Identity)) ||
            objects.Any(item => item.Identity.VolumeSerial != plan.ParentIdentity.VolumeSerial) ||
            !objects.Select(item => item.RelativePath).Order(StringComparer.Ordinal).SequenceEqual(
                plan.Candidate.Snapshot.Entries.Select(item => item.RelativePath).Order(StringComparer.Ordinal)))
            throw new IOException("configuration_workspace_inventory_incomplete");
        PhysicalPathPolicy.NormalizeWithoutLinks(wire.WorkPath);
        return new(plan, wire.WorkPath, FromWire(wire.WorkIdentity), Array.AsReadOnly(objects));
    }

    private static FileTreePhysicalIdentity ToWire(PhysicalObjectIdentity identity) => new()
        { VolumeSerial = identity.VolumeSerial, FileId = identity.FileId };
    private static PhysicalObjectIdentity FromWire(FileTreePhysicalIdentity identity) => new(identity.VolumeSerial, identity.FileId);
}
