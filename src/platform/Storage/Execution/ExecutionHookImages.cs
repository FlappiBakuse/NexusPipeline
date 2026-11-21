using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Execution.Judgement;

namespace NexusPipeline.Platform.Storage.Execution;

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
