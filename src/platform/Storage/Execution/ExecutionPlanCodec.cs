using System.Text.Json;
using System.Security.Cryptography;
using NexusPipeline.Extensibility;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

public sealed class ExecutionPlanCodec(RuntimeInstance instance, ContractReader contracts, ImmutableBlobStore blobs)
{
    public FrozenExecutionPlan Inspect(ExecutionPlan wire, ReadOnlyMemory<byte> definitionBytes) => Inspect(wire, definitionBytes, null);

    internal FrozenExecutionPlan Inspect(ExecutionPlan wire, ReadOnlyMemory<byte> definitionBytes,
        IReadOnlyDictionary<string, byte[]>? providerManifests)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(wire);
        var definitions = definitionBytes.ToArray();
        var held = new List<IDisposable>();
        try
        {
            var plan = Decode(bytes, held, definitions, providerManifests);
            var snapshot = plan.ReadWire();
            PlanInputLease.Acquire(snapshot, held);
            ExecutionPlanResources.RequireCurrent(snapshot);
            return plan;
        }
        finally { Release(held); }
    }

    public PreparedExecutionPlan Freeze(ExecutionPlan wire)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(wire);
        var held = new List<IDisposable>();
        try
        {
            var plan = Decode(bytes, held);
            var snapshot = plan.ReadWire();
            PlanInputLease.Acquire(snapshot, held);
            ExecutionPlanResources.RequireCurrent(snapshot);
            var reference = blobs.PersistPlan(plan.Export());
            held.Add(blobs.OpenVerified(reference));
            return new(plan, reference, held);
        }
        catch { Release(held); throw; }
    }

    public FrozenExecutionPlan Read(BlobReference reference)
    {
        var held = new List<IDisposable>();
        try { return ReadHeld(reference, held); }
        finally { Release(held); }
    }

    internal FrozenExecutionPlan ReadHeld(BlobReference reference, List<IDisposable> held)
    {
        if (reference.RelativePath != $"blobs/plans/{reference.Sha256}.json") throw new IOException("execution_plan_reference_invalid");
        return Decode(ReadBlob(reference, held), held);
    }

    internal ExecutionPlan ReadMetadataHeld(BlobReference reference, List<IDisposable> held)
    {
        if (reference.RelativePath != $"blobs/plans/{reference.Sha256}.json") throw new IOException("execution_plan_reference_invalid");
        return Decode(ReadBlob(reference, held), held, verifyMappingContent: false).ReadWire();
    }

    private FrozenExecutionPlan Decode(byte[] bytes, List<IDisposable> held, byte[]? definitionBytes = null,
        IReadOnlyDictionary<string, byte[]>? providerManifests = null, bool verifyMappingContent = true)
    {
        var plan = contracts.Read<ExecutionPlan>(bytes, "execution-plan.schema.json");
        ExecutionPlanSources.RequireCanonicalId(plan.RunId);
        if (plan.QueueId is not null) ExecutionPlanSources.RequireCanonicalId(plan.QueueId);
        if (plan.InstanceId != instance.InstanceId.ToString("D") || blobs.Instance.InstanceId != instance.InstanceId ||
            Guid.Parse(plan.RunId) == Guid.Empty) throw new IOException("execution_plan_identity_invalid");
        foreach (var root in plan.RootBindings)
            if (Within(root.Path, instance.RuntimeRoot) || Within(instance.RuntimeRoot, root.Path)) throw new IOException("execution_root_overlaps_runtime");
        var content = plan.DefinitionBundle;
        if (content.RelativePath != $"blobs/content/{content.Sha256}.bin") throw new IOException("execution_definition_reference_invalid");
        var definition = definitionBytes ?? ReadBlob(new(content.RelativePath, content.SizeBytes, content.Sha256), held);
        if (definition.LongLength != content.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(definition)) != content.Sha256)
            throw new IOException("execution_definition_reference_invalid");
        var bundle = contracts.Read<AutomationBundle>(definition, "automation-bundle.schema.json");
        ExecutionPlanGraph.Validate(plan);
        JudgementNetworkBinding.Validate(plan);
        ExecutionPlanSources.Validate(plan, bundle);
        foreach (var mapping in ExecutionTargetInputs.MappingReferences(plan, blobs, verifyContent: verifyMappingContent))
            if (verifyMappingContent || mapping.RelativePath.StartsWith("blobs/plans/", StringComparison.Ordinal))
                held.Add(blobs.OpenVerified(mapping));
        ValidateExtensions(plan);
        NexusPipeline.Platform.Storage.Emulation.EmulatorPlanBinding.Require(plan, blobs, contracts);
        foreach (var reference in ExecutionAdapterBinding.Require(plan, bundle, blobs, contracts, providerManifests, verifyMappingContent))
            if (providerManifests is null || !providerManifests.ContainsKey(reference.Sha256)) held.Add(blobs.OpenVerified(reference));
        foreach (var manifest in ExecutionProviderBinding.Require(plan, blobs, contracts, providerManifests))
            if (providerManifests is null) held.Add(blobs.OpenVerified(manifest));
        if (!plan.ResourceKeys.SequenceEqual(ExecutionPlanResources.Derive(plan))) throw new IOException("execution_resource_scope_unbound");
        return new(bytes);
    }

    private byte[] ReadBlob(BlobReference reference, List<IDisposable> held)
    {
        if (reference.SizeBytes is < 1 or > StrictJson.MaximumBytes) throw new IOException("execution_input_size_invalid");
        var input = blobs.OpenVerified(reference);
        held.Add(input);
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static void ValidateExtensions(ExecutionPlan plan)
    {
        var locks = plan.ExtensionLocks.ToDictionary(item => item.ExtensionId, StringComparer.Ordinal);
        var required = plan.Steps.Where(step => step.EngineKind == "extensionWorker" || step.Adapter is not null).Select(step => step.ExtensionId!)
            .Concat(plan.Steps.Select(step => step.Adapter?.CompiledProvider).OfType<string>())
            .Concat(plan.Steps.Select(step => step.Target?.Android?.Emulator?.ExtensionId).OfType<string>())
            .Concat(plan.Steps.Select(step => step.Provider?.ExtensionId).OfType<string>()).ToHashSet(StringComparer.Ordinal);
        if (!required.SetEquals(locks.Keys) || locks.Values.Any(item => item.ContractFingerprint != SdkIdentity.ContractFingerprint))
            throw new IOException("execution_extension_lock_unbound");
    }

    private static void Release(List<IDisposable> held)
    {
        for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
    }

    private static bool Within(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
