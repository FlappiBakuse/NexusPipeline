using NexusPipeline.Core.Automation;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Execution.Judgement;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionHookInputs
{
    internal const int MaximumJournalEvents = 5;
    internal const int MaximumJournalEventBytes = 16384;

    internal static IReadOnlyList<ExecutionPlanHook>? Read(AutomationBinding binding, string workingDirectory,
        FileTreePhysicalIdentity workingDirectoryIdentity, IReadOnlyDictionary<Guid, AutomationRoot> roots, PlanInputLease? inputs = null)
    {
        var hooks = new List<ExecutionPlanHook>();
        void Add(AutomationHook? definition, string role, bool once, bool final)
        {
            if (definition is null) return;
            var launch = ExecutionLaunchInputs.Capture(definition.Executable, definition.Arguments, workingDirectory, workingDirectoryIdentity, roots, inputs);
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

internal static class ExecutionHookImages
{
    internal static ExecutionPlanInterpreterImage Capture(string path, bool runtime, PlanInputLease? inputs = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var temporary = inputs is null ? new PlanInputLease() : null;
        var lease = inputs ?? temporary!;
        path = PhysicalPathPolicy.NormalizeWithoutLinks(path);
        var image = lease.ReadImage(path, runtime ? "judgement_interpreter_size_invalid" : "execution_hook_source_size_invalid");
        if (!runtime) return image;
        var files = RuntimeFiles(image.Path, lease).ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        if (string.Equals(Path.GetFileName(path), "powershell.exe", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(path)!;
            var entries = Directory.EnumerateFiles(parent).Take(4097).ToArray();
            if (entries.Length > 4096) throw new IOException("execution_hook_runtime_directory_limit");
            var assets = entries.Where(file =>
                Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(file).Equals(".config", StringComparison.OrdinalIgnoreCase)).Take(257).ToArray();
            if (assets.Length > 256) throw new IOException("execution_hook_runtime_assets_limit");
            foreach (var asset in assets)
            {
                var relative = Path.GetFileName(asset);
                files[relative] = RuntimeFile(relative, lease.ReadImage(asset, "execution_hook_source_size_invalid"));
            }
        }
        if (files.Count > JudgementRuntimeAssets.MaximumFiles) throw new IOException("execution_hook_runtime_assets_limit");
        return image with { RuntimeFiles = files.Values.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray() };
    }

    internal static IEnumerable<ExecutionPlanRuntimeFile> RuntimeFiles(string path, PlanInputLease inputs)
    {
        var parent = Path.GetDirectoryName(path)!;
        return JudgementRuntimeAssets.Paths(path).Select(relative => RuntimeFile(relative,
            inputs.ReadImage(Path.GetFullPath(Path.Combine(parent, relative)), "judgement_runtime_asset_size_invalid")));
    }

    private static ExecutionPlanRuntimeFile RuntimeFile(string relative, ExecutionPlanInterpreterImage image) => new()
    { RelativePath = relative, SizeBytes = image.SizeBytes, Sha256 = image.Sha256, Identity = image.Identity, ParentIdentity = image.ParentIdentity };

    internal static IEnumerable<(string Path, long Size, string Sha256, FileTreePhysicalIdentity Identity, FileTreePhysicalIdentity Parent)> Files(ExecutionPlanStep step)
    {
        foreach (var hook in step.Hooks ?? [])
            foreach (var image in new[] { hook.Source, hook.Command })
            {
                yield return (image.Path, image.SizeBytes, image.Sha256, image.Identity, image.ParentIdentity);
                foreach (var file in image.RuntimeFiles)
                    yield return (Path.GetFullPath(Path.Combine(Path.GetDirectoryName(image.Path)!, file.RelativePath)),
                        file.SizeBytes, file.Sha256, file.Identity, file.ParentIdentity);
            }
    }

}
