using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ExecutionStepInput(ExecutionPlanStep Step, IReadOnlyList<ExecutionPlanRootBindingsItem> Roots,
    IReadOnlyList<ExecutionPlanConfigurationSlotsItem> ConfigurationSlots);

internal static class ExecutionStepInputs
{
    internal static ExecutionStepInput Read(ExecutionSelectionItem item, IReadOnlyList<ConfigurationRevision> configurations, ImmutableBlobStore blobs,
        ExecutionJudgementRuntime? judgementRuntime = null, PreparedMuMuDiscovery? targetDiscovery = null,
        NexusPipeline.Platform.Serialization.Protocol.EmulatorTargetFrozen? emulator = null, bool initialize = false)
    {
        var held = new List<IDisposable>();
        try
        {
            foreach (var entry in configurations.SelectMany(source => source.Tree.Entries).Where(entry => entry.Kind == FileObjectKind.File)
                .DistinctBy(entry => (entry.Sha256, entry.SizeBytes)))
                blobs.Verify(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!));
            var roots = item.Roots.ToDictionary(root => root.Id);
            var wireRoots = new List<ExecutionPlanRootBindingsItem>();
            foreach (var root in roots.Values)
            {
                if (PhysicalPathPolicy.NormalizeWithoutLinks(root.Path) != root.Path) throw new IOException("execution_directory_not_canonical");
                held.Add(StableDirectoryLease.Acquire(root.Path));
                wireRoots.Add(new() { RootId = root.Id.ToString("D"), Path = root.Path, Identity = Wire(PhysicalObjectIdentity.Read(root.Path)) });
            }
            string Resolve(NexusPipeline.Core.Automation.AutomationPath path) =>
                PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(roots[path.RootId].Path, path.RelativePath));
            var executable = Resolve(item.Script.Executable);
            var parent = Path.GetDirectoryName(executable)!;
            held.Add(StableDirectoryLease.Acquire(parent));
            using var image = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (image.Length is < 1 or > 1073741824) throw new IOException("execution_image_size_invalid");
            var identity = PhysicalObjectIdentity.Read(image.SafeFileHandle);
            var step = new ExecutionPlanStep
            {
                Purpose = initialize ? "initialization" : null,
                StepId = item.StepId, ScriptId = item.Script.Id.ToString("D"),
                UserId = item.User.Id.ToString("D"), BindingId = item.Binding.Id.ToString("D"), EngineKind = "process", DependsOn = [],
                ExecutablePath = executable, ImageSizeBytes = image.Length, ImageSha256 = Convert.ToHexStringLower(SHA256.HashData(image)),
                ImageIdentity = Wire(identity), ParentIdentity = Wire(PhysicalObjectIdentity.Read(parent)), Arguments = item.Script.Arguments,
                WorkingDirectory = roots[item.Script.RootId].Path, WorkingDirectoryIdentity = Wire(PhysicalObjectIdentity.Read(roots[item.Script.RootId].Path)),
                Tasks = [], SelectedTaskIds = [], ConfigurationTargets = item.Script.ConfigurationPaths.Select(Resolve).ToArray(),
                LogTargets = initialize ? [] : item.Script.LogPaths.Select((path, index) => ExecutionLogSelectors.Capture(index,
                    ExecutionLogSelectors.Declaration(roots[path.RootId].Path, path.RelativePath))).ToArray(), UpdateConfigurationAtFinish = !initialize && item.Script.UpdateConfigurationAtFinish,
                JudgementWorker = initialize ? null : ExecutionJudgementImages.Capture(item.Script.Judgement, judgementRuntime),
                Target = initialize ? null : ExecutionTargetInputs.Capture(item.Script.Target, roots, blobs, blobs.Instance.InstanceId, targetDiscovery, emulator),
                Hooks = initialize ? null : ExecutionHookInputs.Read(item.Binding, roots[item.Script.RootId].Path,
                    Wire(PhysicalObjectIdentity.Read(roots[item.Script.RootId].Path)), roots)
            };
            if (PhysicalObjectIdentity.Read(executable) != identity) throw new IOException("execution_image_changed");
            if (configurations.Count != step.ConfigurationTargets.Count) throw new IOException("execution_configuration_slots_unbound");
            var slots = configurations.Select((source, index) => new ExecutionPlanConfigurationSlotsItem
            {
                StepId = step.StepId, BindingId = source.Address.BindingId.ToString("D"), ResourceId = source.Address.ResourceId,
                TargetPath = step.ConfigurationTargets[index], ExpectedRevision = source.Revision
            }).ToArray();
            return new(step, wireRoots, slots);
        }
        finally { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); }
    }

    private static FileTreePhysicalIdentity Wire(PhysicalObjectIdentity identity) => new() { VolumeSerial = identity.VolumeSerial, FileId = identity.FileId };
}
