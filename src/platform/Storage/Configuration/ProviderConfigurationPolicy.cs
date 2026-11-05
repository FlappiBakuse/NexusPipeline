using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Storage.Configuration;

internal static class ProviderConfigurationPolicy
{
    [SupportedOSPlatform("windows")]
    internal static ExecutionProviderRegistry.PreparedExecutionProvider? Acquire(AutomationScript script,
        ExecutionProviderRegistry? providers) => script.Kind == AutomationKind.Generic || !script.AdapterResources.IsEmpty ? null :
        (providers ?? throw new IOException("execution_extension_provider_unavailable"))
            .Acquire(script.ExtensionId ?? throw new ConfigurationException("configuration_provider_owner_unbound"));

    [SupportedOSPlatform("windows")]
    internal static ImmutableArray<ProviderConfigurationDeclaration> Declarations(AutomationScript script,
        ExecutionProviderRegistry.PreparedExecutionProvider provider) => provider.Use(package =>
    {
        if (script.Kind != AutomationKind.Specialized || !script.AdapterResources.IsEmpty || script.ExtensionId != provider.ExtensionId)
            throw new ConfigurationException("configuration_provider_owner_unbound");
        return RequireCoverage(package.Declaration.ConfigurationResources, script.ConfigurationPaths.Length);
    });

    [SupportedOSPlatform("windows")]
    internal static ConfigurationRevision RequireSaved(AutomationBinding binding, AutomationScript script,
        ProviderConfigurationDeclaration declaration, ExecutionProviderRegistry.PreparedExecutionProvider provider,
        ConfigurationRevisionStore revisions) => provider.Use(package =>
    {
        if (binding.ScriptId != script.Id || !Declarations(script, provider).Contains(declaration))
            throw new ConfigurationException("configuration_provider_owner_unbound");
        var saved = revisions.Read(new(binding.Id, declaration.ResourceId));
        ConfigurationRevisionPolicy.RequireCurrentContract(saved, Contract(provider.ExtensionId, package.Declaration.EntryId, declaration));
        if (saved!.UserId != binding.UserId || saved.ScriptId != script.Id)
            throw new ConfigurationException("configuration_owner_changed");
        return saved;
    });

    internal static ConfigurationContractStamp Contract(string extensionId, string entryId,
        ProviderConfigurationDeclaration declaration)
    {
        var frozen = ExecutionProviderPolicy.Freeze(new(entryId, "worker.exe", [], null)
            { ConfigurationResources = [declaration] }).ConfigurationResources.Single();
        ExecutionProviderPolicy.RequireExtensionId(extensionId);
        var digest = Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(new
        {
            format = "nexus.provider.configuration", version = 1, extensionId, entryId,
            resourceId = frozen.ResourceId, declarationIndex = frozen.DeclarationIndex, definitionRevision = frozen.DefinitionRevision
        })));
        return new(frozen.DefinitionRevision, digest, null, false);
    }

    [SupportedOSPlatform("windows")]
    internal static IReadOnlyList<ConfigurationRevision> RequireSaved(ExecutionSelectionItem selection,
        ExecutionProviderRegistry.PreparedExecutionProvider prepared, ConfigurationRevisionStore revisions) => prepared.Use(package =>
    {
        if (selection.Script.Kind == AutomationKind.Generic || selection.Script.ExtensionId != prepared.ExtensionId ||
            selection.Binding.ScriptId != selection.Script.Id || selection.Binding.UserId != selection.User.Id)
            throw new ConfigurationException("configuration_provider_owner_unbound");
        var declarations = RequireCoverage(package.Declaration.ConfigurationResources, selection.Script.ConfigurationPaths.Length);
        return declarations.Select(declaration =>
        {
            var address = new ConfigurationAddress(selection.Binding.Id, declaration.ResourceId);
            var saved = revisions.Read(address);
            ConfigurationRevisionPolicy.RequireCurrentContract(saved, Contract(prepared.ExtensionId, package.Declaration.EntryId, declaration));
            if (saved!.UserId != selection.User.Id || saved.ScriptId != selection.Script.Id)
                throw new ConfigurationException("configuration_owner_changed");
            return saved;
        }).ToArray();
    });

    internal static void RequireFrozenSource(ExecutionPlan total, ExecutionPlanStep step, ConfigurationRevision source,
        ImmutableBlobStore blobs, ContractReader contracts)
    {
        if (step.Adapter is not null || step.Provider is not { } provider || provider.ExtensionId != step.ExtensionId)
            throw new ConfigurationException("configuration_provider_descriptor_missing");
        var reference = new BlobReference(provider.Manifest.RelativePath, provider.Manifest.SizeBytes, provider.Manifest.Sha256);
        StepEvidenceCodec.Plan(reference);
        using var input = blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)reference.SizeBytes)]; input.ReadExactly(bytes);
        var manifest = new ExtensionManifestAdmission(contracts).ReadCurrent(bytes, provider.ExtensionId);
        var descriptor = manifest.Execution ?? throw new ConfigurationException("configuration_provider_descriptor_missing");
        if (descriptor.EntryId != provider.EntryId) throw new ConfigurationException("configuration_provider_descriptor_changed");
        var declarations = RequireCoverage(ReadDeclarations(descriptor.ConfigurationResources), step.ConfigurationTargets.Count);
        var slot = total.ConfigurationSlots.SingleOrDefault(item => item.StepId == step.StepId &&
            item.ResourceId == source.Address.ResourceId) ?? throw new ConfigurationException("configuration_provider_resource_unbound");
        var index = step.ConfigurationTargets.ToList().FindIndex(path => path == slot.TargetPath);
        var declaration = declarations.SingleOrDefault(item => item.DeclarationIndex == index && item.ResourceId == slot.ResourceId)
            ?? throw new ConfigurationException("configuration_provider_resource_unbound");
        ConfigurationRevisionPolicy.RequireCurrentContract(source, Contract(provider.ExtensionId, descriptor.EntryId, declaration));
    }

    internal static void RequireFrozenSlots(ExecutionPlan total, ExecutionPlanStep step,
        IReadOnlyList<ExecutionProviderResource>? resources)
    {
        var declarations = RequireCoverage(ReadDeclarations(resources), step.ConfigurationTargets.Count);
        var slots = total.ConfigurationSlots.Where(slot => slot.StepId == step.StepId).ToArray();
        if (slots.Length != declarations.Length || declarations.Any(declaration => !slots.Any(slot =>
            slot.ResourceId == declaration.ResourceId && slot.TargetPath == step.ConfigurationTargets[declaration.DeclarationIndex] &&
            slot.BindingId == step.BindingId))) throw new ConfigurationException("configuration_provider_resource_unbound");
    }

    internal static ImmutableArray<ProviderConfigurationDeclaration> ReadDeclarations(IReadOnlyList<ExecutionProviderResource>? declarations) =>
        declarations?.Select(item => new ProviderConfigurationDeclaration(item.ResourceId, checked((int)item.DeclarationIndex),
            item.DefinitionRevision)).ToImmutableArray() ?? [];

    internal static ImmutableArray<ProviderConfigurationDeclaration> RequireCoverage(
        ImmutableArray<ProviderConfigurationDeclaration> declarations, int paths)
    {
        declarations = ExecutionProviderPolicy.Freeze(new("main", "worker.exe", [], null)
            { ConfigurationResources = declarations }).ConfigurationResources;
        if (declarations.Length != paths || declarations.Select(item => item.DeclarationIndex).Distinct().Count() != paths ||
            !declarations.Select(item => item.DeclarationIndex).Order().SequenceEqual(Enumerable.Range(0, paths)))
            throw new ConfigurationException("configuration_provider_declaration_coverage");
        return declarations.OrderBy(item => item.DeclarationIndex).ToImmutableArray();
    }
}
