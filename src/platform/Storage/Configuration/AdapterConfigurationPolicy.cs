using System.Security.Cryptography;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Configuration;

public static class AdapterConfigurationPolicy
{
    public static ConfigurationContractStamp Contract(AutomationScript script, AutomationAdapterResource resource)
    {
        AutomationAdapterPolicy.Validate(script);
        if (script.ExtensionId is null || !script.AdapterResources.Contains(resource)) throw new ConfigurationException("configuration_adapter_resource_unbound");
        var target = script.ConfigurationPaths[resource.DeclarationIndex];
        return Contract(script.ExtensionId, resource, target);
    }

    internal static ConfigurationContractStamp Contract(string extensionId, AutomationAdapterResource resource, AutomationPath target)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(new
        {
            format = "nexus.adapter.configuration", version = 1, extensionId,
            resource.ResourceId, resource.DeclarationIndex, resource.RelativePath, resource.DefinitionRevision,
            resource.Syntax, access = ConfigurationSelectorProtocol.Write(resource.Access),
            targetRootId = target.RootId, targetRelativePath = target.RelativePath
        })));
        return new(resource.DefinitionRevision, digest, null, false);
    }

    public static ConfigurationRevision RequireSaved(AutomationBinding binding, AutomationScript script,
        AutomationAdapterResource resource, ConfigurationRevisionStore revisions)
    {
        var address = RequireAddress(binding, script, resource);
        return RequireRevision(binding, script, resource, address, revisions.Read(address));
    }

    internal static ConfigurationRevision RequireStored(AutomationBinding binding, AutomationScript script,
        AutomationAdapterResource resource, StoredConfigurationRevision stored) =>
        RequireRevision(binding, script, resource, RequireAddress(binding, script, resource), stored.Revision);

    private static ConfigurationAddress RequireAddress(AutomationBinding binding, AutomationScript script, AutomationAdapterResource resource)
    {
        if (binding.ScriptId != script.Id) throw new ConfigurationException("configuration_adapter_owner_unbound");
        return new(binding.Id, resource.ResourceId);
    }

    private static ConfigurationRevision RequireRevision(AutomationBinding binding, AutomationScript script,
        AutomationAdapterResource resource, ConfigurationAddress address, ConfigurationRevision? saved)
    {
        ConfigurationRevisionPolicy.RequireCurrentContract(saved, Contract(script, resource));
        if (saved!.Address != address || saved.UserId != binding.UserId || saved.ScriptId != script.Id)
            throw new ConfigurationException("configuration_owner_changed");
        if (!saved.Tree.Entries.Any(entry => entry.Kind == FileObjectKind.File && entry.RelativePath == resource.RelativePath))
            throw new ConfigurationException("configuration_adapter_source_missing");
        return saved;
    }
}
