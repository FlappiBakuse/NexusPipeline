using System.Collections.Immutable;

namespace NexusPipeline.Core.Automation;

public sealed record BindingScriptSource(string CandidateId, Guid RootId, AutomationPath Executable,
    ImmutableArray<string> Arguments, ImmutableArray<AutomationPath> ConfigurationPaths, ImmutableArray<AutomationPath> LogPaths)
{
    public static AutomationScript Resolve(AutomationScript script, AutomationBinding binding)
    {
        if (binding.Source is not { } source) return script;
        if (binding.ScriptId != script.Id || script.Kind != AutomationKind.Specialized || source.RootId != script.RootId ||
            source.ConfigurationPaths.Length != script.AdapterResources.Length ||
            source.ConfigurationPaths.Concat(source.LogPaths).Append(source.Executable).Any(path =>
                path.RootId != script.RootId || !AutomationCatalogPolicy.RelativePath(path.RelativePath)))
            throw new AutomationDefinitionException("automation_binding_source_invalid");
        return script with { Executable = source.Executable, Arguments = source.Arguments,
            ConfigurationPaths = source.ConfigurationPaths, LogPaths = source.LogPaths };
    }
}
