using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Core.Automation;

public static class AutomationAdapterPolicy
{
    public static void Validate(AutomationScript script)
    {
        var resources = script.AdapterResources;
        if (resources.IsDefault || resources.Length > RunRequestPolicy.MaximumResources ||
            resources.Any(resource => resource is null)) throw new AutomationDefinitionException("automation_adapter_resources_invalid");
        if (resources.IsEmpty) return;
        if (script.Kind != AutomationKind.Specialized || resources.Length != script.ConfigurationPaths.Length ||
            resources.Select(resource => resource.ResourceId).Distinct(StringComparer.Ordinal).Count() != resources.Length ||
            !resources.Select(resource => resource.DeclarationIndex).Order().SequenceEqual(Enumerable.Range(0, resources.Length)))
            throw new AutomationDefinitionException("automation_adapter_resource_coverage_invalid");
        foreach (var resource in resources)
        {
            if (string.IsNullOrWhiteSpace(resource.ResourceId) || resource.ResourceId.Length > 120 || resource.ResourceId.Any(char.IsControl) ||
                !FileTreePolicy.RelativePath(resource.RelativePath) || resource.DefinitionRevision is < 1 or > 9007199254740991)
                throw new AutomationDefinitionException("automation_adapter_resource_invalid");
            if (!Enum.IsDefined(resource.Syntax)) throw new AutomationDefinitionException("automation_adapter_syntax_invalid");
            _ = ConfigurationPatchPolicy.Freeze(resource.Access);
        }
    }
}
