using System.Security.Cryptography;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class PlanInputLease
{
    internal static void Acquire(ExecutionPlan plan, List<IDisposable> held)
    {
        var workerFiles = new Dictionary<string, (long Size, string Sha256, FileTreePhysicalIdentity Identity, FileTreePhysicalIdentity Parent)>(StringComparer.OrdinalIgnoreCase);
        if (plan.CaptureRuntime is { } capture) AcquireImage(capture.Broker, held);
        foreach (var root in plan.RootBindings) Directory(root.Path, root.Identity, held);
        foreach (var step in plan.Steps)
        {
            Directory(step.WorkingDirectory, step.WorkingDirectoryIdentity, held);
            if (step.Target?.Pc is { } pc) Directory(pc.Launch.WorkingDirectory, pc.Launch.WorkingDirectoryIdentity, held);
            Directory(Path.GetDirectoryName(step.ExecutablePath)!, step.ParentIdentity, held);
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(step.ExecutablePath);
            if (path != step.ExecutablePath) throw new IOException("execution_image_path_not_canonical");
            var image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            held.Add(image);
            if (PhysicalObjectIdentity.Read(image.SafeFileHandle) != FileTreeProtocol.FromWire(step.ImageIdentity) ||
                image.Length != step.ImageSizeBytes || Convert.ToHexStringLower(SHA256.HashData(image)) != step.ImageSha256)
                throw new IOException("execution_image_changed");
            image.Position = 0;
            if (step.Provider is { } provider) Directory(provider.PackageRoot, provider.PackageRootIdentity, held);
            foreach (var providerImage in ExecutionProviderInputs.Images(step)) AcquireImage(providerImage, held);
            if (step.Target?.Android?.Emulator is { } emulator) Directory(emulator.InstanceDirectory, emulator.InstancePhysicalId, held);
            foreach (var file in ExecutionJudgementImages.Files(step).Concat(ExecutionHookImages.Files(step)).Concat(ExecutionTargetInputs.Files(step)).Concat(ExecutionAdapterBinding.SourceFiles(step)))
            {
                var expected = (file.Size, file.Sha256, file.Identity, file.Parent);
                if (workerFiles.TryGetValue(file.Path, out var previous) && previous != expected)
                    throw new IOException("execution_judgement_image_conflict");
                workerFiles[file.Path] = expected;
            }
        }
        foreach (var (path, expected) in workerFiles)
            File(path, expected.Size, expected.Sha256, expected.Identity, expected.Parent, held);
    }

    internal static void AcquireImage(ExecutionPlanInterpreterImage image, List<IDisposable> held)
    {
        File(image.Path, image.SizeBytes, image.Sha256, image.Identity, image.ParentIdentity, held);
        foreach (var file in image.RuntimeFiles)
            File(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(image.Path)!, file.RelativePath)),
                file.SizeBytes, file.Sha256, file.Identity, file.ParentIdentity, held);
    }

    private static void File(string path, long size, string sha256, FileTreePhysicalIdentity identity,
        FileTreePhysicalIdentity parent, List<IDisposable> held)
    {
        if (PhysicalPathPolicy.NormalizeWithoutLinks(path) != path) throw new IOException("execution_judgement_path_not_canonical");
        Directory(Path.GetDirectoryName(path)!, parent, held);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); held.Add(file);
        if (file.Length != size || PhysicalObjectIdentity.Read(file.SafeFileHandle) != FileTreeProtocol.FromWire(identity) ||
            Convert.ToHexStringLower(SHA256.HashData(file)) != sha256) throw new IOException("execution_judgement_image_changed");
        file.Position = 0;
    }

    private static void Directory(string path, FileTreePhysicalIdentity identity, List<IDisposable> held)
    {
        if (PhysicalPathPolicy.NormalizeWithoutLinks(path) != path) throw new IOException("execution_directory_not_canonical");
        held.Add(StableDirectoryLease.Acquire(path));
        if (PhysicalObjectIdentity.Read(path) != FileTreeProtocol.FromWire(identity)) throw new IOException("execution_directory_changed");
    }
}
