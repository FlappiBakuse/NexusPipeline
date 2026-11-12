using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Maintenance;

namespace NexusPipeline.Platform.Storage.Journals;

public enum PayloadAction { Install, Upgrade, Remove }
public sealed record PayloadApplyRoots
{
    public string HostPayloadRoot { get; }
    public string ExtensionPayloadRoot { get; }

    public PayloadApplyRoots(string HostPayloadRoot, string ExtensionPayloadRoot)
    {
        this.HostPayloadRoot = PhysicalPathPolicy.NormalizeWithoutLinks(Path.TrimEndingDirectorySeparator(HostPayloadRoot));
        this.ExtensionPayloadRoot = PhysicalPathPolicy.NormalizeWithoutLinks(Path.TrimEndingDirectorySeparator(ExtensionPayloadRoot));
    }
}
public sealed record FrozenPayloadApply(Guid InstanceId, Guid OperationId, Guid JournalId, JournalKind Kind,
    PayloadAction Action, string? ExtensionId, string TargetPath, PhysicalObjectIdentity ParentIdentity,
    ArchivedFileTree Before, ArchivedFileTree Candidate, IReadOnlyList<string> ResourceKeys)
{
    public BlobReference? ExtensionSource { get; init; }
    public BlobReference? MaintenanceSource { get; init; }
}

public sealed class PayloadPlanCodec(RuntimeInstance instance, ContractReader contracts, ImmutableBlobStore blobs, PayloadApplyRoots roots)
{
    public PreparedPayloadPlan Freeze(FrozenPayloadApply plan)
    {
        Validate(plan);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ToWire(plan));
        var frozen = Decode(bytes, out _);
        var held = new List<FileStream>();
        try
        {
            foreach (var reference in ContentInputs(frozen).OrderBy(item => item.Sha256, StringComparer.Ordinal))
                held.Add(blobs.OpenVerified(reference));
            if (frozen.MaintenanceSource is { } sourceReference)
            {
                var source = new UpdateSourceCodec(instance, contracts).ReadMetadata(sourceReference);
                foreach (var reference in UpdateSourceCodec.References(source).Prepend(sourceReference).Distinct())
                    held.Add(blobs.OpenVerified(reference));
            }
            var planReference = blobs.PersistPlan(bytes);
            held.Add(blobs.OpenVerified(planReference));
            return new(frozen, planReference, held);
        }
        catch
        {
            foreach (var input in held) input.Dispose();
            throw;
        }
    }

    public FrozenPayloadApply Read(BlobReference reference)
    {
        var plan = ReadStructure(reference);
        foreach (var blob in ContentInputs(plan)) blobs.Verify(blob);
        if (plan.MaintenanceSource is { } sourceReference)
        {
            var source = new UpdateSourceCodec(instance, contracts).ReadMetadata(sourceReference);
            foreach (var sourceInput in UpdateSourceCodec.References(source)) blobs.Verify(sourceInput);
        }
        return plan;
    }

    internal IReadOnlyList<BlobReference> ContentInputs(FrozenPayloadApply plan)
    {
        if (plan.Kind != JournalKind.ExtensionApply) return plan.Before.Contents.Concat(plan.Candidate.Contents).Distinct().ToArray();
        var source = new PayloadSourceCodec(instance, contracts).ReadMetadata(plan.ExtensionSource ?? throw new IOException("extension_payload_source_required"));
        return source.Package is { } package ? [package.Archive] : [];
    }

    internal FrozenPayloadApply ReadStructure(BlobReference reference) => ReadStructure(reference, out _);

    internal FrozenPayloadApply ReadStructure(BlobReference reference, out ExtensionPayloadSource? source)
    {
        if (reference.SizeBytes > StrictJson.MaximumBytes || reference.RelativePath != $"blobs/plans/{reference.Sha256}.json")
            throw new IOException("payload_plan_reference_invalid");
        using var input = blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return Decode(bytes, out source);
    }

    private FrozenPayloadApply Decode(ReadOnlyMemory<byte> bytes, out ExtensionPayloadSource? source)
    {
        var wire = contracts.Read<PayloadApplyPlan>(bytes, "payload-apply-plan.schema.json");
        var identities = Array.AsReadOnly(wire.OriginalObjects.Select(item => new PhysicalTreeObject(item.RelativePath, FileTreeProtocol.FromWire(item.Identity))).ToArray());
        var plan = new FrozenPayloadApply(Guid.Parse(wire.InstanceId), Guid.Parse(wire.OperationId), Guid.Parse(wire.JournalId),
            Enum.Parse<JournalKind>(wire.OperationKind, true), Enum.Parse<PayloadAction>(wire.Action, true), wire.ExtensionId,
            wire.TargetPath, FileTreeProtocol.FromWire(wire.ParentIdentity), FileTreeProtocol.FromWire(wire.Before, identities),
            FileTreeProtocol.FromWire(wire.Candidate, []), Array.AsReadOnly(wire.ResourceKeys.ToArray()))
        {
            ExtensionSource = wire.ExtensionSource is null ? null : new(wire.ExtensionSource.RelativePath,
                wire.ExtensionSource.SizeBytes, wire.ExtensionSource.Sha256),
            MaintenanceSource = wire.MaintenanceSource is null ? null : new(wire.MaintenanceSource.RelativePath,
                wire.MaintenanceSource.SizeBytes, wire.MaintenanceSource.Sha256)
        };
        source = Validate(plan);
        return plan;
    }

    private ExtensionPayloadSource? Validate(FrozenPayloadApply plan)
    {
        ExtensionPayloadSource? source = null;
        if (plan.InstanceId != instance.InstanceId || blobs.Instance.InstanceId != instance.InstanceId ||
            plan.OperationId == Guid.Empty || plan.JournalId == Guid.Empty || !Enum.IsDefined(plan.Action) ||
            plan.Kind is not (JournalKind.UpdateApply or JournalKind.ExtensionApply)) throw new IOException("payload_plan_identity_invalid");
        FileTreeArchive.ValidateArchive(plan.Before);
        FileTreeArchive.ValidateArchive(plan.Candidate);
        var target = PhysicalPathPolicy.NormalizeWithoutLinks(plan.TargetPath);
        var expected = plan.Kind == JournalKind.UpdateApply ? roots.HostPayloadRoot : Path.Combine(roots.ExtensionPayloadRoot, plan.ExtensionId ?? "");
        if (target != plan.TargetPath || target != PhysicalPathPolicy.NormalizeWithoutLinks(expected) || target == Path.GetPathRoot(target) ||
            plan.Kind == JournalKind.ExtensionApply && IsWithin(target, instance.RuntimeRoot) ||
            plan.Kind == JournalKind.UpdateApply && IsWithin(instance.RuntimeRoot, target) ||
            plan.Kind == JournalKind.ExtensionApply && string.IsNullOrWhiteSpace(plan.ExtensionId)) throw new IOException("payload_target_not_owned");
        if (plan.Kind == JournalKind.UpdateApply)
        {
            ApplicationTree.Validate(plan.Before.Snapshot, complete: false);
            ApplicationTree.Validate(plan.Candidate.Snapshot, complete: true);
            if (ApplicationTree.Roots.Select(name => Path.Combine(target, name)).Any(path =>
                IsWithin(path, instance.RuntimeRoot) || IsWithin(instance.RuntimeRoot, path))) throw new IOException("payload_target_not_owned");
            var sourceCodec = new UpdateSourceCodec(instance, contracts);
            if (plan.MaintenanceSource is null) throw new IOException("maintenance_update_source_required");
            sourceCodec.RequireFrozenBinding(sourceCodec.ReadMetadata(plan.MaintenanceSource), plan);
        }
        else
        {
            if (plan.MaintenanceSource is not null) throw new IOException("payload_resource_scope_unbound");
            var sourceCodec = new PayloadSourceCodec(instance, contracts);
            if (plan.ExtensionSource is null) throw new IOException("extension_payload_source_required");
            source = sourceCodec.ReadMetadata(plan.ExtensionSource);
            sourceCodec.RequireFrozenBinding(source, plan);
        }
        var identities = plan.Before.OriginalObjects;
        if (identities.Count != plan.Before.Snapshot.Entries.Count || identities.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != identities.Count ||
            identities.Any(item => item.Identity.VolumeSerial != plan.ParentIdentity.VolumeSerial || !plan.Before.Snapshot.Entries.Any(entry => entry.RelativePath == item.RelativePath)))
            throw new IOException("payload_original_identity_incomplete");
        return source;
    }

    private static bool IsWithin(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static PayloadApplyPlan ToWire(FrozenPayloadApply plan) => new()
    {
        Format = "nexus.payload.apply-plan", Version = 1, InstanceId = plan.InstanceId.ToString("D"),
        OperationId = plan.OperationId.ToString("D"), JournalId = plan.JournalId.ToString("D"),
        OperationKind = plan.Kind == JournalKind.UpdateApply ? "updateApply" : "extensionApply", Action = plan.Action.ToString().ToLowerInvariant(),
        ExtensionId = plan.ExtensionId, TargetPath = plan.TargetPath, ParentIdentity = FileTreeProtocol.ToWire(plan.ParentIdentity),
        ExtensionSource = plan.ExtensionSource is null ? null : new()
            { RelativePath = plan.ExtensionSource.RelativePath, SizeBytes = plan.ExtensionSource.SizeBytes, Sha256 = plan.ExtensionSource.Sha256 },
        MaintenanceSource = plan.MaintenanceSource is null ? null : new()
            { RelativePath = plan.MaintenanceSource.RelativePath, SizeBytes = plan.MaintenanceSource.SizeBytes, Sha256 = plan.MaintenanceSource.Sha256 },
        Before = FileTreeProtocol.ToWire(plan.Before.Snapshot), Candidate = FileTreeProtocol.ToWire(plan.Candidate.Snapshot), ResourceKeys = plan.ResourceKeys,
        OriginalObjects = plan.Before.OriginalObjects.Select(item => new PayloadApplyPlanOriginalObjectsItem
            { RelativePath = item.RelativePath, Identity = FileTreeProtocol.ToWire(item.Identity) }).ToArray()
    };
}
