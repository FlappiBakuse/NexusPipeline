using System.Security.Cryptography;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Configuration;

public static class GenericConfigurationPolicy
{
    private const string ResourcePrefix = "generic-";

    public static string ResourceId(AutomationPath path) => ResourcePrefix + DefinitionDigest(path);

    public static ConfigurationContractStamp Contract(AutomationPath path) => new(1, DefinitionDigest(path), null, false);

    public static ConfigurationRevision RequireSaved(AutomationBinding binding, AutomationScript script,
        AutomationPath path, ConfigurationRevisionStore revisions)
    {
        var (address, contract) = RequireDefinition(binding, script, path);
        return RequireRevision(binding, address, contract, revisions.Read(address));
    }

    internal static ConfigurationRevision RequireStored(AutomationBinding binding, AutomationScript script,
        AutomationPath path, StoredConfigurationRevision stored)
    {
        var (address, contract) = RequireDefinition(binding, script, path);
        return RequireRevision(binding, address, contract, stored.Revision);
    }

    private static (ConfigurationAddress Address, ConfigurationContractStamp Contract) RequireDefinition(
        AutomationBinding binding, AutomationScript script, AutomationPath path)
    {
        if (script.Kind != AutomationKind.Generic || binding.ScriptId != script.Id || !script.ConfigurationPaths.Contains(path))
            throw new ConfigurationException("configuration_generic_definition_unbound");
        var contract = Contract(path);
        return (new(binding.Id, ResourcePrefix + contract.DefinitionSha256), contract);
    }

    private static ConfigurationRevision RequireRevision(AutomationBinding binding, ConfigurationAddress address,
        ConfigurationContractStamp contract, ConfigurationRevision? saved)
    {
        ConfigurationRevisionPolicy.RequireCurrentContract(saved, contract);
        if (saved!.Address != address || saved.UserId != binding.UserId || saved.ScriptId != binding.ScriptId)
            throw new ConfigurationException("configuration_owner_changed");
        return saved;
    }

    private static string DefinitionDigest(AutomationPath path)
    {
        if (path is null || path.RootId == Guid.Empty || !AutomationCatalogPolicy.RelativePath(path.RelativePath))
            throw new ConfigurationException("configuration_generic_path_invalid");
        return Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(new
        {
            format = "nexus.generic.configuration", version = 1, rootId = path.RootId.ToString("D"), relative = path.RelativePath
        })));
    }
}
