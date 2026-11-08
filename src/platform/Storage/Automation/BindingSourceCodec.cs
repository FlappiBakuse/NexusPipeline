using System.Collections.Immutable;
using NexusPipeline.Core.Automation;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Automation;

internal static class BindingSourceCodec
{
    internal static Core.Automation.BindingScriptSource? Read(NexusPipeline.Platform.Serialization.Protocol.AutomationBindingSource? value) => value is null ? null :
        new(value.CandidateId, Guid.Parse(value.RootId), Path(value.Executable), value.Arguments.ToImmutableArray(),
            value.ConfigurationPaths.Select(Path).ToImmutableArray(), value.LogPaths.Select(Path).ToImmutableArray());

    internal static NexusPipeline.Platform.Serialization.Protocol.AutomationBindingSource? Write(Core.Automation.BindingScriptSource? value) => value is null ? null :
        new() { CandidateId = value.CandidateId, RootId = value.RootId.ToString("D"), Executable = Path(value.Executable),
            Arguments = value.Arguments, ConfigurationPaths = value.ConfigurationPaths.Select(Path).ToArray(), LogPaths = value.LogPaths.Select(Path).ToArray() };

    internal static AutomationScriptScript Resolve(AutomationBundle bundle, ExecutionPlanStep step)
        => Resolve(bundle.Scripts.Single(item => item.Id == step.ScriptId), bundle.Bindings.SingleOrDefault(item => item.Id == step.BindingId)?.Source);

    internal static AutomationScriptScript Resolve(AutomationScriptScript script, NexusPipeline.Platform.Serialization.Protocol.AutomationBindingSource? source)
    {
        if (source is null) return script;
        var binding = new AutomationBinding(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.Parse(script.Id), true, 0) { Source = Read(source) };
        return AutomationBundleCodec.Script(Core.Automation.BindingScriptSource.Resolve(AutomationBundleCodec.Script(script), binding));
    }

    private static AutomationPath Path(AutomationPathsPortablePath value) => new(Guid.Parse(value.RootId), value.Relative);
    private static AutomationPathsPortablePath Path(AutomationPath value) => new() { RootId = value.RootId.ToString("D"), Relative = value.RelativePath };
}
