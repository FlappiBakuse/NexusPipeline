using System.Security.Cryptography;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionAdapterBinding
{
    internal static IReadOnlyList<BlobReference> Require(ExecutionPlan plan, AutomationBundle bundle,
        ImmutableBlobStore blobs, ContractReader contracts, IReadOnlyDictionary<string, byte[]>? previewInputs = null, bool verifyCompiledContents = true)
    {
        var held = new List<BlobReference>();
        foreach (var step in plan.Steps)
        {
            var script = bundle.Scripts.Single(item => item.Id == step.ScriptId);
            if (step.Adapter is not { } adapter)
            {
                if (script.AdapterResources.Count != 0) throw Invalid();
                continue;
            }
            if (script.Kind != "specialized" || script.ExtensionId != step.ExtensionId || script.AdapterResources.Count == 0 ||
                adapter.Resources.Count != script.AdapterResources.Count || adapter.Resources.Select(item => item.ResourceId).Distinct(StringComparer.Ordinal).Count() != adapter.Resources.Count)
                throw Invalid();
            var locked = plan.ExtensionLocks.SingleOrDefault(item => item.ExtensionId == step.ExtensionId) ?? throw Invalid();
            var manifestBytes = Read(adapter.Manifest, blobs, previewInputs);
            _ = Read(adapter.Script, blobs, previewInputs);
            var manifest = new ExtensionManifestAdmission(contracts).ReadCurrent(manifestBytes, step.ExtensionId!);
            if (manifest.Kind is not ("data-specialized" or "managed-code") || !manifest.Capabilities.Contains("task.adapt", StringComparer.Ordinal) ||
                locked.ManifestSha256 != adapter.Manifest.Sha256 || locked.PackageVersion != manifest.PackageVersion ||
                locked.SdkVersion != manifest.SdkVersion || locked.ContractFingerprint != manifest.ContractFingerprint ||
                manifest.Kind == "data-specialized" && manifest.Execution is not null) throw Invalid();
            if ((step.ExclusiveProgram == true) != (manifest.Specialized?.ExclusiveProgram == true)) throw Invalid();
            ExecutionProviderRelationship.Require(manifest, step);
            var sourceRoot = plan.RootBindings.Single(root => root.RootId == script.RootId).Path;
            var sourceFiles = adapter.SourceFiles ?? [];
            if (sourceFiles.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sourceFiles.Count ||
                sourceFiles.Sum(file => file.Content.SizeBytes) > 32 * 1024 * 1024 ||
                sourceFiles.Count == 0 && manifest.Specialized?.ReadOnlySources is not null) throw Invalid();
            foreach (var file in sourceFiles)
            {
                if (!AdapterSourceInputs.Allows(manifest.Specialized?.ReadOnlySources, file.RelativePath) ||
                    file.Path != Path.GetFullPath(Path.Combine(sourceRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)))) throw Invalid();
                var content = file.Content;
                var reference = SourceReference(content);
                using var input = blobs.OpenVerified(reference);
                held.Add(reference);
            }
            var slots = plan.ConfigurationSlots.Where(slot => slot.StepId == step.StepId).ToArray();
            foreach (var task in step.Tasks.Where(task => task.Projection is not null))
                if (!adapter.Resources.Any(resource => resource.ResourceId == task.Projection!.ResourceId &&
                    resource.RelativePath == task.Projection.RelativePath)) throw Invalid();
            foreach (var resource in adapter.Resources)
            {
                var declared = script.AdapterResources.SingleOrDefault(item => item.ResourceId == resource.ResourceId && item.RelativePath == resource.RelativePath) ?? throw Invalid();
                if (declared.DeclarationIndex >= step.ConfigurationTargets.Count || !slots.Any(slot => slot.ResourceId == resource.ResourceId &&
                    slot.TargetPath == step.ConfigurationTargets[checked((int)declared.DeclarationIndex)] && slot.ExpectedRevision == resource.Revision)) throw Invalid();
            }
            var footprint = new BlobReference(adapter.InstalledFootprint.RelativePath, adapter.InstalledFootprint.SizeBytes, adapter.InstalledFootprint.Sha256);
            StepEvidenceCodec.Plan(footprint); held.Add(footprint);
            held.Add(Reference(adapter.Manifest)); held.Add(Reference(adapter.Script));
            if (ExecutionCompilationBinding.Require(plan, step, script.RootId, manifest, blobs, contracts, verifyCompiledContents) is { } compiled)
                held.AddRange(compiled.Closure);
        }
        return held.Distinct().ToArray();
    }

    internal static IEnumerable<BlobReference> References(ExecutionPlan total, ImmutableBlobStore blobs) => total.Steps.Where(step => step.Adapter is not null)
        .SelectMany(step => new[] { Reference(step.Adapter!.Manifest), Reference(step.Adapter.Script),
            new BlobReference(step.Adapter.InstalledFootprint.RelativePath, step.Adapter.InstalledFootprint.SizeBytes, step.Adapter.InstalledFootprint.Sha256) })
        .Concat(total.Steps.SelectMany(step => step.Adapter?.SourceFiles ?? []).Select(file => SourceReference(file.Content)))
        .Concat(ExecutionCompilationBinding.References(total, blobs)).Distinct();

    internal static void RequireFrozenConfiguration(ExecutionPlan total, ExecutionPlanStep step, ConfigurationRevision source,
        ImmutableBlobStore blobs, ContractReader contracts)
    {
        var resource = step.Adapter?.Resources.SingleOrDefault(item => item.ResourceId == source.Address.ResourceId) ?? throw Invalid();
        var entry = source.Tree.Entries.SingleOrDefault(item => item.Kind == FileObjectKind.File && item.RelativePath == resource.RelativePath) ?? throw Invalid();
        if (source.Revision != resource.Revision || entry.Sha256 != resource.Sha256) throw Invalid();
        var reference = total.DefinitionBundle;
        using var input = blobs.OpenVerified(new(reference.RelativePath, reference.SizeBytes, reference.Sha256));
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        var bundle = contracts.Read<AutomationBundle>(bytes, "automation-bundle.schema.json");
        var script = bundle.Scripts.Single(item => item.Id == step.ScriptId);
        var declaration = script.AdapterResources.SingleOrDefault(item => item.ResourceId == resource.ResourceId && item.RelativePath == resource.RelativePath) ?? throw Invalid();
        var target = script.ConfigurationPaths[checked((int)declaration.DeclarationIndex)];
        var contract = AdapterConfigurationPolicy.Contract(script.ExtensionId!, new AutomationAdapterResource(declaration.ResourceId,
            checked((int)declaration.DeclarationIndex), declaration.RelativePath, declaration.DefinitionRevision, Enum.Parse<ConfigurationSyntax>(declaration.Syntax, true),
            ConfigurationSelectorProtocol.Read(declaration.Access)), new(Guid.Parse(target.RootId), target.Relative));
        ConfigurationRevisionPolicy.RequireCurrentContract(source, contract);
    }

    internal static IEnumerable<(string Path, long Size, string Sha256, FileTreePhysicalIdentity Identity, FileTreePhysicalIdentity Parent)> SourceFiles(ExecutionPlanStep step)
        => (step.Adapter?.SourceFiles ?? []).Select(file => (file.Path, file.Content.SizeBytes, file.Content.Sha256, file.Identity, file.ParentIdentity));

    private static byte[] Read(ExecutionPlanContentReference value, ImmutableBlobStore blobs, IReadOnlyDictionary<string, byte[]>? preview)
    {
        var reference = Reference(value); byte[] bytes;
        if (preview is not null && preview.TryGetValue(reference.Sha256, out var captured)) bytes = captured.ToArray();
        else
        {
            using var input = blobs.OpenVerified(reference); bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        }
        if (bytes.LongLength != reference.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != reference.Sha256) throw Invalid();
        return bytes;
    }

    private static BlobReference SourceReference(ExecutionAdapterSourceFileContent value)
    {
        if (value.RelativePath != $"blobs/content/{value.Sha256}.bin" || value.SizeBytes is < 1 or > 8388608) throw Invalid();
        return new(value.RelativePath, value.SizeBytes, value.Sha256);
    }
    private static BlobReference Reference(ExecutionPlanContentReference value)
    {
        if (value.RelativePath != $"blobs/content/{value.Sha256}.bin" || value.SizeBytes is < 1 or > 2097152) throw Invalid();
        return new(value.RelativePath, value.SizeBytes, value.Sha256);
    }
    private static IOException Invalid() => new("execution_adapter_frozen_source_unbound");
}
