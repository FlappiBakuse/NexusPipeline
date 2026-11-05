using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed record FrozenConfigurationExchange(Guid InstanceId, Guid OperationId, Guid JournalId, JournalKind Kind,
    string TargetPath, PhysicalObjectIdentity ParentIdentity, ArchivedFileTree Before, ArchivedFileTree Candidate)
{
    public EditWritePolicy? EditPolicy { get; init; }
    public ConfigurationEditorCommand? Editor { get; init; }
    public BlobReference? RunState { get; init; }
    public ImmutableArray<ResourceClaim> PhysicalClaims { get; init; } = [];
}

public sealed class ConfigurationPlanCodec(RuntimeInstance instance, ContractReader contracts, ImmutableBlobStore blobs)
{
    public PreparedConfigurationPlan Freeze(FrozenConfigurationExchange plan)
    {
        Validate(plan);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ToWire(plan));
        var frozen = Decode(bytes);
        VerifyContents(frozen);
        var held = new List<FileStream>();
        try
        {
            foreach (var reference in References(frozen).OrderBy(item => item.Sha256, StringComparer.Ordinal))
                held.Add(blobs.OpenVerified(reference));
            var referenceToPlan = blobs.PersistPlan(bytes);
            held.Add(blobs.OpenVerified(referenceToPlan));
            return new(frozen, referenceToPlan, held);
        }
        catch
        {
            foreach (var input in held) input.Dispose();
            throw;
        }
    }

    public FrozenConfigurationExchange Read(BlobReference reference)
    {
        var plan = ReadStructure(reference);
        VerifyContents(plan);
        return plan;
    }

    internal FrozenConfigurationExchange ReadStructure(BlobReference reference)
    {
        if (reference.SizeBytes > StrictJson.MaximumBytes || reference.RelativePath != $"blobs/plans/{reference.Sha256}.json")
            throw new IOException("configuration_plan_reference_invalid");
        using var input = blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return Decode(bytes);
    }

    internal FrozenConfigurationExchange ReadHeld(BlobReference reference, List<IDisposable> held, bool metadataOnly,
        out FrozenRunState? state)
    {
        if (reference.SizeBytes is < 1 or > StrictJson.MaximumBytes || reference.RelativePath != $"blobs/plans/{reference.Sha256}.json")
            throw new IOException("configuration_plan_reference_invalid");
        var input = blobs.OpenVerified(reference);
        held.Add(input);
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        var plan = Decode(bytes, held, out state);
        if (metadataOnly) return plan;
        var references = plan.Before.Contents.Concat(plan.Candidate.Contents);
        if (state is not null) references = references.Concat(RunStateCodec.Contents(state));
        foreach (var content in references.Distinct()) held.Add(blobs.OpenVerified(content));
        if (state is not null)
            new ConfigurationTaskTransformation(blobs).ValidateSelected(state.ConfigurationOperationId, state.Source, state.Tasks, plan.Candidate.Snapshot, inputTree: state.InputTree);
        return plan;
    }

    private FrozenConfigurationExchange Decode(ReadOnlyMemory<byte> bytes) => Decode(bytes, null, out _);

    private FrozenConfigurationExchange Decode(ReadOnlyMemory<byte> bytes, List<IDisposable>? held, out FrozenRunState? state)
    {
        var wire = contracts.Read<ConfigurationExchangePlan>(bytes, "configuration-exchange-plan.schema.json");
        var identities = Array.AsReadOnly(wire.OriginalObjects.Select(item => new PhysicalTreeObject(item.RelativePath, FileTreeProtocol.FromWire(item.Identity))).ToArray());
        var plan = new FrozenConfigurationExchange(Guid.Parse(wire.InstanceId), Guid.Parse(wire.OperationId), Guid.Parse(wire.JournalId),
            Enum.Parse<JournalKind>(wire.OperationKind, true), wire.TargetPath, FileTreeProtocol.FromWire(wire.ParentIdentity),
            FileTreeProtocol.FromWire(wire.Before, identities), FileTreeProtocol.FromWire(wire.Candidate, []))
            { RunState = wire.RunState is null ? null : new(wire.RunState.RelativePath, wire.RunState.SizeBytes, wire.RunState.Sha256),
                Editor = wire.Editor is null ? null : EditorCommandPolicy.Freeze(new(wire.Editor.ExecutablePath, wire.Editor.ExecutableSha256,
                    wire.Editor.Arguments.ToImmutableArray(), wire.Editor.WorkingDirectory) { ExclusiveProgram = wire.Editor.ExclusiveProgram == true }),
                PhysicalClaims = wire.PhysicalClaims?.Select(item => new ResourceClaim(item.ResourceKey, Enum.Parse<ResourceClaimMode>(item.Mode, true))).ToImmutableArray() ?? [],
                EditPolicy = wire.EditPolicy is null ? null : EditWriteRules.Freeze(new(
                wire.EditPolicy.WritableRoots.ToImmutableArray(), wire.EditPolicy.ResultShapes.Select(value => Enum.Parse<FileObjectKind>(value, true)).ToImmutableArray())
                { Adapter = wire.EditPolicy.Adapter is null ? null : new(wire.EditPolicy.Adapter.ExtensionId,
                    wire.EditPolicy.Adapter.ManifestSha256, wire.EditPolicy.Adapter.ScriptSha256, wire.EditPolicy.Adapter.ResourceId,
                    wire.EditPolicy.Adapter.RelativePath, wire.EditPolicy.Adapter.Field, wire.EditPolicy.Adapter.PrepareValue, wire.EditPolicy.Adapter.CommitValue),
                  Documents = wire.EditPolicy.Documents?.Select(rule => new EditDocumentRule(rule.RelativePath,
                    Enum.Parse<ConfigurationSyntax>(rule.Syntax, true), rule.Required)).ToImmutableArray() ?? [] }) };
        state = plan.RunState is null ? null : held is null
            ? new RunStateCodec(instance, contracts, blobs).ReadStructure(plan.RunState)
            : new RunStateCodec(instance, contracts, blobs).ReadHeld(plan.RunState, held);
        Validate(plan, state);
        return plan;
    }

    private void Validate(FrozenConfigurationExchange plan, FrozenRunState? state = null)
    {
        if (plan.InstanceId != instance.InstanceId || plan.OperationId == Guid.Empty || plan.JournalId == Guid.Empty ||
            plan.Kind is not (JournalKind.ConfigExchange or JournalKind.ConfigEdit))
            throw new IOException("configuration_plan_identity_invalid");
        var target = PhysicalPathPolicy.NormalizeWithoutLinks(plan.TargetPath);
        var root = Path.GetPathRoot(target)!;
        if (target == root || target != plan.TargetPath || IsWithin(instance.RuntimeRoot, target) || IsWithin(target, instance.RuntimeRoot))
            throw new IOException("configuration_plan_target_invalid");
        FileTreeArchive.ValidateArchive(plan.Before);
        FileTreeArchive.ValidateArchive(plan.Candidate);
        if (plan.RunState is not null)
        {
            state ??= new RunStateCodec(instance, contracts, blobs).ReadStructure(plan.RunState);
            if (plan.Kind != JournalKind.ConfigExchange || state.ConfigurationOperationId != plan.OperationId)
                throw new IOException("configuration_run_state_plan_unbound");
        }
        if (plan.EditPolicy is not null)
        {
            if (plan.Kind != JournalKind.ConfigEdit && plan.RunState is null) throw new IOException("configuration_editor_policy_kind_invalid");
            EditWriteRules.Freeze(plan.EditPolicy);
        }
        if (plan.Editor is not null)
        {
            EditorCommandPolicy.Freeze(plan.Editor);
            if (plan.Kind != JournalKind.ConfigEdit || plan.EditPolicy is null || plan.PhysicalClaims.IsDefaultOrEmpty ||
                PhysicalPathPolicy.NormalizeSyntax(plan.Editor.ExecutablePath) != plan.Editor.ExecutablePath ||
                PhysicalPathPolicy.NormalizeSyntax(plan.Editor.WorkingDirectory) != plan.Editor.WorkingDirectory)
                throw new IOException("configuration_editor_command_unbound");
        }
        if (plan.PhysicalClaims.IsDefault || plan.PhysicalClaims.Length > 4096 ||
            plan.PhysicalClaims.Any(claim => claim is null || !Enum.IsDefined(claim.Mode) || claim.ResourceKey.Length > 512 ||
                !claim.ResourceKey.StartsWith("filesystem:", StringComparison.Ordinal) || claim.ResourceKey.Any(char.IsControl)) ||
            plan.PhysicalClaims.Select(item => item.ResourceKey).Distinct(StringComparer.Ordinal).Count() != plan.PhysicalClaims.Length ||
            !plan.PhysicalClaims.SequenceEqual(plan.PhysicalClaims.OrderBy(item => item.ResourceKey, StringComparer.Ordinal)) ||
            plan.PhysicalClaims.Length != 0 && plan.Editor is null && plan.RunState is null)
            throw new IOException("configuration_physical_claims_invalid");
        var identities = plan.Before.OriginalObjects;
        if (identities.Count != plan.Before.Snapshot.Entries.Count ||
            identities.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != identities.Count ||
            identities.Any(item => item.Identity.VolumeSerial != plan.ParentIdentity.VolumeSerial ||
                !plan.Before.Snapshot.Entries.Any(entry => entry.RelativePath == item.RelativePath)))
            throw new IOException("configuration_plan_original_identity_incomplete");
    }

    private static bool IsWithin(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private void VerifyContents(FrozenConfigurationExchange plan)
    {
        foreach (var blob in References(plan)) blobs.Verify(blob);
        if (plan.RunState is null) return;
        var state = new RunStateCodec(instance, contracts, blobs).Read(plan.RunState);
        new ConfigurationTaskTransformation(blobs).ValidateSelected(state.ConfigurationOperationId, state.Source, state.Tasks, plan.Candidate.Snapshot, inputTree: state.InputTree);
    }

    internal IReadOnlyList<BlobReference> References(FrozenConfigurationExchange plan)
    {
        var references = plan.Before.Contents.Concat(plan.Candidate.Contents);
        if (plan.RunState is { } reference)
        {
            var state = new RunStateCodec(instance, contracts, blobs).ReadStructure(reference);
            if (plan.Kind != JournalKind.ConfigExchange || state.ConfigurationOperationId != plan.OperationId)
                throw new IOException("configuration_run_state_plan_unbound");
            references = references.Concat(RunStateCodec.References(state)).Append(reference);
        }
        return references.Distinct().ToArray();
    }

    private static ConfigurationExchangePlan ToWire(FrozenConfigurationExchange plan) => new()
    {
        Format = "nexus.configuration.exchange-plan", Version = 1,
        InstanceId = plan.InstanceId.ToString("D"), OperationId = plan.OperationId.ToString("D"), JournalId = plan.JournalId.ToString("D"),
        OperationKind = plan.Kind == JournalKind.ConfigExchange ? "configExchange" : "configEdit", TargetPath = plan.TargetPath,
        RunState = plan.RunState is null ? null : new() { RelativePath = plan.RunState.RelativePath, SizeBytes = plan.RunState.SizeBytes, Sha256 = plan.RunState.Sha256 },
        ParentIdentity = FileTreeProtocol.ToWire(plan.ParentIdentity), Before = FileTreeProtocol.ToWire(plan.Before.Snapshot), Candidate = FileTreeProtocol.ToWire(plan.Candidate.Snapshot),
        Editor = plan.Editor is null ? null : new() { ExclusiveProgram = plan.Editor.ExclusiveProgram ? true : null, ExecutablePath = plan.Editor.ExecutablePath, ExecutableSha256 = plan.Editor.ExecutableSha256,
            Arguments = plan.Editor.Arguments, WorkingDirectory = plan.Editor.WorkingDirectory },
        PhysicalClaims = plan.PhysicalClaims.IsDefaultOrEmpty ? null : plan.PhysicalClaims.Select(item => new ConfigurationExchangePlanPhysicalClaimsItem
            { ResourceKey = item.ResourceKey, Mode = item.Mode.ToString().ToLowerInvariant() }).ToArray(),
        EditPolicy = plan.EditPolicy is null ? null : new() { WritableRoots = plan.EditPolicy.WritableRoots,
            Adapter = plan.EditPolicy.Adapter is null ? null : new() { ExtensionId = plan.EditPolicy.Adapter.ExtensionId,
                ManifestSha256 = plan.EditPolicy.Adapter.ManifestSha256, ScriptSha256 = plan.EditPolicy.Adapter.ScriptSha256,
                ResourceId = plan.EditPolicy.Adapter.ResourceId, RelativePath = plan.EditPolicy.Adapter.RelativePath,
                Field = plan.EditPolicy.Adapter.Field, PrepareValue = plan.EditPolicy.Adapter.PrepareValue, CommitValue = plan.EditPolicy.Adapter.CommitValue },
            Documents = plan.EditPolicy.Documents.IsEmpty ? null : plan.EditPolicy.Documents.Select(rule => new ConfigurationExchangePlanEditPolicyDocumentsItem
                { RelativePath = rule.RelativePath, Syntax = WireSyntax(rule.Syntax), Required = rule.Required }).ToArray(),
            ResultShapes = plan.EditPolicy.ResultShapes.Select(shape => shape.ToString().ToLowerInvariant()).ToArray() },
        OriginalObjects = plan.Before.OriginalObjects.Select(item => new ConfigurationExchangePlanOriginalObjectsItem
            { RelativePath = item.RelativePath, Identity = FileTreeProtocol.ToWire(item.Identity) }).ToArray()
    };

    private static string WireSyntax(ConfigurationSyntax syntax)
    {
        var text = syntax.ToString();
        return char.ToLowerInvariant(text[0]) + text[1..];
    }

}
