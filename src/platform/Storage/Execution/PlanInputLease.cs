using System.Security.Cryptography;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class PlanInputLease
{
    private sealed record InputFile(long Size, string Sha256, FileTreePhysicalIdentity Identity, FileTreePhysicalIdentity Parent);

    internal static void Acquire(ExecutionPlan plan, List<IDisposable> held)
    {
        var files = new Dictionary<string, InputFile>(StringComparer.OrdinalIgnoreCase);
        if (plan.CaptureRuntime is { } capture) AcquireImage(capture.Broker, held, files);
        foreach (var root in plan.RootBindings) Directory(root.Path, root.Identity, held);
        foreach (var step in plan.Steps)
        {
            Directory(step.WorkingDirectory, step.WorkingDirectoryIdentity, held);
            if (step.Target?.Pc is { } pc) Directory(pc.Launch.WorkingDirectory, pc.Launch.WorkingDirectoryIdentity, held);
            File(step.ExecutablePath, step.ImageSizeBytes, step.ImageSha256, step.ImageIdentity, step.ParentIdentity, held, files, executable: true);
            if (step.Provider is { } provider) Directory(provider.PackageRoot, provider.PackageRootIdentity, held);
            foreach (var providerImage in ExecutionProviderInputs.Images(step)) AcquireImage(providerImage, held, files);
            if (step.Target?.Android?.Emulator is { } emulator) Directory(emulator.InstanceDirectory, emulator.InstancePhysicalId, held);
            foreach (var file in ExecutionJudgementImages.Files(step).Concat(ExecutionHookImages.Files(step)).Concat(ExecutionTargetInputs.Files(step)).Concat(ExecutionAdapterBinding.SourceFiles(step)))
                File(file.Path, file.Size, file.Sha256, file.Identity, file.Parent, held, files);
        }
    }

    internal static void AcquireImage(ExecutionPlanInterpreterImage image, List<IDisposable> held)
        => AcquireImage(image, held, new(StringComparer.OrdinalIgnoreCase));

    private static void AcquireImage(ExecutionPlanInterpreterImage image, List<IDisposable> held, Dictionary<string, InputFile> files)
    {
        File(image.Path, image.SizeBytes, image.Sha256, image.Identity, image.ParentIdentity, held, files);
        foreach (var file in image.RuntimeFiles)
            File(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(image.Path)!, file.RelativePath)),
                file.SizeBytes, file.Sha256, file.Identity, file.ParentIdentity, held, files);
    }

    private static void File(string path, long size, string sha256, FileTreePhysicalIdentity identity,
        FileTreePhysicalIdentity parent, List<IDisposable> held, Dictionary<string, InputFile> files, bool executable = false)
    {
        var expected = new InputFile(size, sha256, identity, parent);
        if (files.TryGetValue(path, out var previous))
        {
            if (previous != expected) throw new IOException(executable ? "execution_image_changed" : "execution_judgement_image_conflict");
            return;
        }
        if (PhysicalPathPolicy.NormalizeWithoutLinks(path) != path)
            throw new IOException(executable ? "execution_image_path_not_canonical" : "execution_judgement_path_not_canonical");
        Directory(Path.GetDirectoryName(path)!, parent, held);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); held.Add(file);
        if (file.Length != size || PhysicalObjectIdentity.Read(file.SafeFileHandle) != FileTreeProtocol.FromWire(identity) ||
            Convert.ToHexStringLower(SHA256.HashData(file)) != sha256)
            throw new IOException(executable ? "execution_image_changed" : "execution_judgement_image_changed");
        file.Position = 0;
        files.Add(path, expected);
    }

    private static void Directory(string path, FileTreePhysicalIdentity identity, List<IDisposable> held)
    {
        if (PhysicalPathPolicy.NormalizeWithoutLinks(path) != path) throw new IOException("execution_directory_not_canonical");
        held.Add(StableDirectoryLease.Acquire(path));
        if (PhysicalObjectIdentity.Read(path) != FileTreeProtocol.FromWire(identity)) throw new IOException("execution_directory_changed");
    }
}
