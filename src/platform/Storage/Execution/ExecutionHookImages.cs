using System.Security.Cryptography;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Execution.Judgement;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionHookImages
{
    internal static ExecutionPlanInterpreterImage Capture(string path, bool runtime)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!runtime) return Plain(path);
        var inspected = JudgementInterpreterLease.Inspect(path);
        var files = inspected.RuntimeFiles.Select(file => new ExecutionPlanRuntimeFile
        {
            RelativePath = file.RelativePath, SizeBytes = file.SizeBytes, Sha256 = file.Sha256,
            Identity = Wire(file.Identity), ParentIdentity = Wire(file.ParentIdentity)
        }).ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        if (string.Equals(Path.GetFileName(path), "powershell.exe", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(path)!;
            using var held = StableDirectoryLease.Acquire(parent);
            var entries = Directory.EnumerateFiles(parent).Take(4097).ToArray();
            if (entries.Length > 4096) throw new IOException("execution_hook_runtime_directory_limit");
            var assets = entries.Where(file =>
                Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(file).Equals(".config", StringComparison.OrdinalIgnoreCase)).Take(257).ToArray();
            if (assets.Length > 256) throw new IOException("execution_hook_runtime_assets_limit");
            foreach (var asset in assets)
            {
                var file = Plain(asset);
                files[Path.GetFileName(asset)] = new()
                {
                    RelativePath = Path.GetFileName(asset), SizeBytes = file.SizeBytes, Sha256 = file.Sha256,
                    Identity = file.Identity, ParentIdentity = file.ParentIdentity
                };
            }
        }
        if (files.Count > JudgementRuntimeAssets.MaximumFiles) throw new IOException("execution_hook_runtime_assets_limit");
        return new()
        {
            Path = inspected.Path, SizeBytes = inspected.SizeBytes, Sha256 = inspected.Sha256,
            Identity = Wire(inspected.Identity), ParentIdentity = Wire(inspected.ParentIdentity),
            RuntimeFiles = files.Values.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray()
        };
    }

    private static ExecutionPlanInterpreterImage Plain(string requested)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(requested);
        var parent = Path.GetDirectoryName(path)!;
        using var directory = StableDirectoryLease.Acquire(parent);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > 1073741824) throw new IOException("execution_hook_source_size_invalid");
        return new()
        {
            Path = path, SizeBytes = input.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(input)),
            Identity = Wire(PhysicalObjectIdentity.Read(input.SafeFileHandle)), ParentIdentity = Wire(PhysicalObjectIdentity.Read(parent)), RuntimeFiles = []
        };
    }

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

    private static FileTreePhysicalIdentity Wire(PhysicalObjectIdentity identity) => new() { VolumeSerial = identity.VolumeSerial, FileId = identity.FileId };
}
