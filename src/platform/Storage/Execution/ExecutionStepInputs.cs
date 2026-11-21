using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ExecutionStepInput(ExecutionPlanStep Step, IReadOnlyList<ExecutionPlanRootBindingsItem> Roots,
    IReadOnlyList<ExecutionPlanConfigurationSlotsItem> ConfigurationSlots);

internal static class ExecutionStepInputs
{
    internal static ExecutionStepInput Read(ExecutionSelectionItem item, IReadOnlyList<ConfigurationRevision> configurations, ImmutableBlobStore blobs, PlanInputLease inputs,
        ExecutionJudgementRuntime? judgementRuntime = null, PreparedMuMuDiscovery? targetDiscovery = null,
        NexusPipeline.Platform.Serialization.Protocol.EmulatorTargetFrozen? emulator = null, bool initialize = false)
    {
        foreach (var entry in configurations.SelectMany(source => source.Tree.Entries).Where(entry => entry.Kind == FileObjectKind.File)
            .DistinctBy(entry => (entry.Sha256, entry.SizeBytes)))
            blobs.Verify(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!));
        var roots = item.Roots.ToDictionary(root => root.Id);
        var wireRoots = new List<ExecutionPlanRootBindingsItem>();
        foreach (var root in roots.Values)
        {
            wireRoots.Add(new() { RootId = root.Id.ToString("D"), Path = root.Path, Identity = inputs.HoldDirectory(root.Path) });
        }
        string Resolve(NexusPipeline.Core.Automation.AutomationPath path) =>
            PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(roots[path.RootId].Path, path.RelativePath));
        var executable = Resolve(item.Script.Executable);
        var image = inputs.ReadImage(executable, "execution_image_size_invalid");
        var workingDirectory = roots[item.Script.RootId].Path;
        var workingDirectoryIdentity = inputs.HoldDirectory(workingDirectory);
        var step = new ExecutionPlanStep
        {
            Purpose = initialize ? "initialization" : null,
            StepId = item.StepId, ScriptId = item.Script.Id.ToString("D"),
            UserId = item.User.Id.ToString("D"), BindingId = item.Binding.Id.ToString("D"), EngineKind = "process", DependsOn = [],
            ExecutablePath = executable, ImageSizeBytes = image.SizeBytes, ImageSha256 = image.Sha256,
            ImageIdentity = image.Identity, ParentIdentity = image.ParentIdentity, Arguments = item.Script.Arguments,
            WorkingDirectory = workingDirectory, WorkingDirectoryIdentity = workingDirectoryIdentity,
            Tasks = [], SelectedTaskIds = [], ConfigurationTargets = item.Script.ConfigurationPaths.Select(Resolve).ToArray(),
            LogTargets = initialize ? [] : item.Script.LogPaths.Select((path, index) => ExecutionLogSelectors.Capture(index,
                ExecutionLogSelectors.Declaration(roots[path.RootId].Path, path.RelativePath))).ToArray(), UpdateConfigurationAtFinish = !initialize && item.Script.UpdateConfigurationAtFinish,
            JudgementWorker = initialize ? null : ExecutionJudgementImages.Capture(item.Script.Judgement, judgementRuntime, inputs),
            Target = initialize ? null : ExecutionTargetInputs.Capture(item.Script.Target, roots, blobs, blobs.Instance.InstanceId, targetDiscovery, emulator, inputs),
            Hooks = initialize ? null : ExecutionHookInputs.Read(item.Binding, workingDirectory, workingDirectoryIdentity, roots, inputs)
        };
        if (configurations.Count != step.ConfigurationTargets.Count) throw new IOException("execution_configuration_slots_unbound");
        var slots = configurations.Select((source, index) => new ExecutionPlanConfigurationSlotsItem
        {
            StepId = step.StepId, BindingId = source.Address.BindingId.ToString("D"), ResourceId = source.Address.ResourceId,
            TargetPath = step.ConfigurationTargets[index], ExpectedRevision = source.Revision
        }).ToArray();
        return new(step, wireRoots, slots);
    }
}
