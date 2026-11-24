using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Extensions;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed record ConfigurationResourceView(string ResourceId, int DeclarationIndex, string Label, long Revision,
    bool IsSaved, FileObjectKind Shape, int FileCount, string RelativePath, ConfigurationSyntax? Syntax);
public sealed record ConfigurationOverview(Guid BindingId, long BindingRevision, Guid ScriptId, long ScriptRevision,
    AutomationKind Kind, string? ExtensionId, IReadOnlyList<ConfigurationResourceView> Resources);

[SupportedOSPlatform("windows")]
public sealed class ConfigurationOverviewReader(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
    ContractReader contracts, ExecutionProviderRegistry? providers = null)
{
    public ConfigurationOverview Read(Guid bindingId) => lease.PublishExecution(() =>
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot || lease.Instance != instance)
            throw new IOException("configuration_overview_instance_unbound");
        var catalog = new AutomationDefinitionStore(database).Read();
        var binding = catalog.Bindings.SingleOrDefault(item => item.Id == bindingId)
            ?? throw new ConfigurationException("configuration_binding_missing");
        var script = Core.Automation.BindingScriptSource.Resolve(catalog.Scripts.Single(item => item.Id == binding.ScriptId), binding);
        var revisions = new ConfigurationRevisionStore(instance, database, contracts);
        var resources = new List<ConfigurationResourceView>();
        using var provider = ProviderConfigurationPolicy.Acquire(script, providers);
        if (script.Kind == AutomationKind.Generic)
        {
            foreach (var (path, index) in script.ConfigurationPaths.Select((path, index) => (path, index)))
            {
                var id = GenericConfigurationPolicy.ResourceId(path);
                var stored = revisions.ReadStored(new(binding.Id, id));
                var saved = stored is null ? null : GenericConfigurationPolicy.RequireStored(binding, script, path, stored);
                resources.Add(View(id, index, path.RelativePath, "", null, saved));
            }
        }
        else if (provider is not null)
        {
            foreach (var declaration in ProviderConfigurationPolicy.Declarations(script, provider))
            {
                var stored = revisions.ReadStored(new(binding.Id, declaration.ResourceId));
                var saved = stored is null ? null : ProviderConfigurationPolicy.RequireStored(binding, script, declaration, provider, stored);
                resources.Add(View(declaration.ResourceId, declaration.DeclarationIndex,
                    script.ConfigurationPaths[declaration.DeclarationIndex].RelativePath, "", null, saved));
            }
        }
        else foreach (var resource in script.AdapterResources)
        {
            var stored = revisions.ReadStored(new(binding.Id, resource.ResourceId));
            var saved = stored is null ? null : AdapterConfigurationPolicy.RequireStored(binding, script, resource, stored);
            resources.Add(View(resource.ResourceId, resource.DeclarationIndex,
                script.ConfigurationPaths[resource.DeclarationIndex].RelativePath, resource.RelativePath, resource.Syntax, saved));
        }
        var result = new ConfigurationOverview(binding.Id, binding.Revision, script.Id, script.Revision, script.Kind, script.ExtensionId, resources.AsReadOnly());
        return provider is null ? result : provider.Use(_ => result);
    });

    private static ConfigurationResourceView View(string id, int index, string label, string relative,
        ConfigurationSyntax? syntax, ConfigurationRevision? saved) => new(id, index, label, saved?.Revision ?? 0,
            saved is not null, saved?.Tree.Shape ?? FileObjectKind.Missing,
            saved?.Tree.Entries.Count(item => item.Kind == FileObjectKind.File) ?? 0, relative, syntax);
}
