using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Maintenance;
using System.Text.Json;

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
    private sealed record VerifiedSource(BlobReference Reference, ExtensionPayloadSource? Extension, HostUpdateSource? Maintenance);

    public PreparedPayloadPlan Freeze(FrozenPayloadApply plan)
    {
        var held = new List<FileStream>();
        var sourceFiles = new List<FileStream>();
        try
        {
            var source = Validate(plan, sourceFiles);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(ToWire(plan));
            var frozen = Decode(bytes, out var verified, previouslyRead: source);
            foreach (var reference in ContentInputs(frozen, verified).OrderBy(item => item.Sha256, StringComparer.Ordinal))
                held.Add(blobs.OpenVerified(reference));
            if (verified.Maintenance is { } maintenance)
            {
                held.AddRange(sourceFiles);
                sourceFiles.Clear();
                foreach (var reference in UpdateSourceCodec.References(maintenance).Where(item => item != verified.Reference).Distinct())
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
        finally { foreach (var input in sourceFiles) input.Dispose(); }
    }

    public FrozenPayloadApply Read(BlobReference reference)
    {
        var sourceFiles = new List<FileStream>();
        try
        {
            var plan = ReadStructure(reference, sourceFiles, out var source);
            foreach (var blob in ContentInputs(plan, source)) blobs.Verify(blob);
            if (source.Maintenance is { } maintenance)
                foreach (var sourceInput in UpdateSourceCodec.References(maintenance)) blobs.Verify(sourceInput);
            return plan;
        }
        finally { foreach (var input in sourceFiles) input.Dispose(); }
    }

    private static IReadOnlyList<BlobReference> ContentInputs(FrozenPayloadApply plan, VerifiedSource verified)
    {
        if (plan.Kind != JournalKind.ExtensionApply) return plan.Before.Contents.Concat(plan.Candidate.Contents).Distinct().ToArray();
        var source = verified.Extension ?? throw new IOException("extension_payload_source_required");
        if (source.RemovalReason == "files-changed") return plan.Before.Contents;
        return source.Package is { } package ? [package.Archive] : [];
    }

    internal FrozenPayloadApply ReadStructure(BlobReference reference) => ReadStructure(reference, out _);

    internal FrozenPayloadApply ReadStructure(BlobReference reference, out ExtensionPayloadSource? source)
    {
        var plan = ReadStructure(reference, null, out var verified);
        source = verified.Extension;
        return plan;
    }

    private FrozenPayloadApply ReadStructure(BlobReference reference, List<FileStream>? sourceFiles, out VerifiedSource verified)
    {
        if (reference.SizeBytes > StrictJson.MaximumBytes || reference.RelativePath != $"blobs/plans/{reference.Sha256}.json")
            throw new IOException("payload_plan_reference_invalid");
        using var input = blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return Decode(bytes, out verified, sourceFiles);
    }

    private FrozenPayloadApply Decode(ReadOnlyMemory<byte> bytes, out VerifiedSource source,
        List<FileStream>? sourceFiles = null, VerifiedSource? previouslyRead = null)
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
        source = Validate(plan, sourceFiles, previouslyRead);
        return plan;
    }

    private VerifiedSource Validate(FrozenPayloadApply plan, List<FileStream>? sourceFiles, VerifiedSource? previouslyRead = null)
    {
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
        VerifiedSource verified;
        if (plan.Kind == JournalKind.UpdateApply)
        {
            ApplicationTree.Validate(plan.Before.Snapshot, complete: false);
            ApplicationTree.Validate(plan.Candidate.Snapshot, complete: true);
            if (ApplicationTree.Roots.Select(name => Path.Combine(target, name)).Any(path =>
                IsWithin(path, instance.RuntimeRoot) || IsWithin(instance.RuntimeRoot, path))) throw new IOException("payload_target_not_owned");
            var sourceCodec = new UpdateSourceCodec(instance, contracts);
            var reference = plan.MaintenanceSource ?? throw new IOException("maintenance_update_source_required");
            if (previouslyRead is not null && (previouslyRead.Reference != reference || previouslyRead.Maintenance is null))
                throw new IOException("maintenance_update_source_unbound");
            var source = previouslyRead?.Maintenance ?? sourceCodec.ReadMetadata(reference, sourceFiles);
            sourceCodec.RequireFrozenBinding(source, plan);
            verified = previouslyRead ?? new(reference, null, source);
        }
        else
        {
            if (plan.MaintenanceSource is not null) throw new IOException("payload_resource_scope_unbound");
            var sourceCodec = new PayloadSourceCodec(instance, contracts);
            var reference = plan.ExtensionSource ?? throw new IOException("extension_payload_source_required");
            if (previouslyRead is not null && (previouslyRead.Reference != reference || previouslyRead.Extension is null))
                throw new IOException("extension_payload_source_unbound");
            var source = previouslyRead?.Extension ?? sourceCodec.ReadMetadata(reference, sourceFiles);
            sourceCodec.RequireFrozenBinding(source, plan);
            verified = previouslyRead ?? new(reference, source, null);
        }
        var identities = plan.Before.OriginalObjects;
        var paths = plan.Before.Snapshot.Entries.Select(entry => entry.RelativePath).ToHashSet(StringComparer.Ordinal);
        if (identities.Count != plan.Before.Snapshot.Entries.Count || identities.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != identities.Count ||
            identities.Any(item => item.Identity.VolumeSerial != plan.ParentIdentity.VolumeSerial || !paths.Contains(item.RelativePath)))
            throw new IOException("payload_original_identity_incomplete");
        return verified;
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

public sealed class PreparedPayloadPlan : IDisposable
{
    private List<FileStream>? _held;
    private readonly FrozenPayloadApply _plan;
    private readonly BlobReference _reference;

    internal PreparedPayloadPlan(FrozenPayloadApply plan, BlobReference reference, List<FileStream> held)
        { _plan = plan; _reference = reference; _held = held; }

    public FrozenPayloadApply Plan
    {
        get { ObjectDisposedException.ThrowIf(Volatile.Read(ref _held) is null, this); return _plan; }
    }

    public BlobReference Reference
    {
        get { ObjectDisposedException.ThrowIf(Volatile.Read(ref _held) is null, this); return _reference; }
    }

    public void Dispose()
    {
        var held = Interlocked.Exchange(ref _held, null);
        if (held is null) return;
        for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
    }
}
