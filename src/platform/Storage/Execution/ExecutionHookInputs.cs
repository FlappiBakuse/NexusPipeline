using NexusPipeline.Core.Automation;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionHookInputs
{
    internal const int MaximumJournalEvents = 5;
    internal const int MaximumJournalEventBytes = 16384;

    internal static IReadOnlyList<ExecutionPlanHook>? Read(AutomationBinding binding, string workingDirectory,
        FileTreePhysicalIdentity workingDirectoryIdentity, IReadOnlyDictionary<Guid, AutomationRoot> roots)
    {
        var hooks = new List<ExecutionPlanHook>();
        void Add(AutomationHook? definition, string role, bool once, bool final)
        {
            if (definition is null) return;
            var launch = ExecutionLaunchInputs.Capture(definition.Executable, definition.Arguments, workingDirectory, workingDirectoryIdentity, roots);
            hooks.Add(new()
            {
                Role = role, LaunchKind = launch.LaunchKind, Source = launch.Source, Command = launch.Command,
                SourceArguments = launch.SourceArguments, Arguments = launch.Arguments,
                WorkingDirectory = launch.WorkingDirectory, WorkingDirectoryIdentity = launch.WorkingDirectoryIdentity,
                OnceAfterSuccess = once, FinalOnly = final
            });
        }
        Add(binding.PreHook, "preHook", binding.PreOnceAfterSuccess, false);
        Add(binding.PostHook, "postHook", false, binding.PostFinalOnly);
        return hooks.Count == 0 ? null : hooks.ToArray();
    }

    internal static void Require(ExecutionPlanStep step, AutomationRelationsBinding? binding,
        IReadOnlyDictionary<string, ExecutionPlanRootBindingsItem> roots)
    {
        var hooks = step.Hooks ?? [];
        var expected = new[] { (Role: "preHook", Definition: binding?.PreHook), (Role: "postHook", Definition: binding?.PostHook) }
            .Where(item => item.Definition is not null).ToArray();
        if (hooks.Count != expected.Length || !hooks.Select(hook => hook.Role).SequenceEqual(expected.Select(item => item.Role)))
            throw new IOException("execution_hook_source_unbound");
        for (var index = 0; index < hooks.Count; index++)
        {
            var hook = hooks[index]; var definition = expected[index].Definition!;
            ExecutionLaunchInputs.Require(new(hook.LaunchKind, hook.Source, hook.Command, hook.SourceArguments, hook.Arguments,
                hook.WorkingDirectory, hook.WorkingDirectoryIdentity), ExecutionLaunchInputs.Resolve(definition.Executable, roots),
                definition.Arguments, step.WorkingDirectory, step.WorkingDirectoryIdentity);
            if (hook.OnceAfterSuccess != (hook.Role == "preHook" && binding!.PreOnceAfterSuccess == true) ||
                hook.FinalOnly != (hook.Role == "postHook" && binding!.PostFinalOnly == true) ||
                hook.Role == "preHook" && hook.FinalOnly || hook.Role == "postHook" && hook.OnceAfterSuccess)
                throw new IOException("execution_hook_policy_unbound");
        }
        if (binding is { PreHook: null, PreOnceAfterSuccess: true } or { PostHook: null, PostFinalOnly: true })
            throw new IOException("execution_hook_policy_unbound");
    }
}
