using System.Security.Cryptography;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Configuration;

public static class GenericConfigurationPolicy
{
    public static string ResourceId(AutomationPath path) => "generic-" + DefinitionDigest(path);

    public static ConfigurationContractStamp Contract(AutomationPath path) => new(1, DefinitionDigest(path), null, false);

    public static ConfigurationRevision RequireSaved(AutomationBinding binding, AutomationScript script,
        AutomationPath path, ConfigurationRevisionStore revisions)
    {
        if (script.Kind != AutomationKind.Generic || binding.ScriptId != script.Id || !script.ConfigurationPaths.Contains(path))
            throw new ConfigurationException("configuration_generic_definition_unbound");
        var saved = revisions.Read(new(binding.Id, ResourceId(path)));
        ConfigurationRevisionPolicy.RequireCurrentContract(saved, Contract(path));
        if (saved!.UserId != binding.UserId || saved.ScriptId != script.Id)
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
