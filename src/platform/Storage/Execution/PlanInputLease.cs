using System.Security.Cryptography;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class PlanInputLease : IDisposable
{
    private sealed record InputFile(long Size, string Sha256, FileTreePhysicalIdentity Identity, FileTreePhysicalIdentity Parent);
    private readonly List<IDisposable> _held = [];
    private readonly Dictionary<string, InputFile> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileTreePhysicalIdentity> _directories = new(StringComparer.Ordinal);
    private bool _disposed;

    internal static void Acquire(ExecutionPlan plan, List<IDisposable> held)
    {
        var inputs = new PlanInputLease();
        try { inputs.Require(plan); held.Add(inputs); }
        catch { inputs.Dispose(); throw; }
    }

    internal void Require(ExecutionPlan plan)
    {
        if (plan.CaptureRuntime is { } capture)
        {
            ExecutionCaptureInputs.RequireCurrentHost(capture);
            RequireImage(capture.Broker);
        }
        foreach (var root in plan.RootBindings) RequireDirectory(root.Path, root.Identity);
        foreach (var step in plan.Steps)
        {
            RequireDirectory(step.WorkingDirectory, step.WorkingDirectoryIdentity);
            if (step.Target?.Pc is { } pc) RequireDirectory(pc.Launch.WorkingDirectory, pc.Launch.WorkingDirectoryIdentity);
            RequireFile(step.ExecutablePath, step.ImageSizeBytes, step.ImageSha256, step.ImageIdentity, step.ParentIdentity, executable: true);
            if (step.Provider is { } provider) RequireDirectory(provider.PackageRoot, provider.PackageRootIdentity);
            foreach (var providerImage in ExecutionProviderInputs.Images(step)) RequireImage(providerImage);
            if (step.Target?.Android?.Emulator is { } emulator) RequireDirectory(emulator.InstanceDirectory, emulator.InstancePhysicalId);
            foreach (var file in ExecutionJudgementImages.Files(step).Concat(ExecutionHookImages.Files(step)).Concat(ExecutionTargetInputs.Files(step)).Concat(ExecutionAdapterBinding.SourceFiles(step)))
                RequireFile(file.Path, file.Size, file.Sha256, file.Identity, file.Parent);
        }
    }

    internal static void AcquireImage(ExecutionPlanInterpreterImage image, List<IDisposable> held)
    {
        var inputs = new PlanInputLease();
        try { inputs.RequireImage(image); held.Add(inputs); }
        catch { inputs.Dispose(); throw; }
    }

    private void RequireImage(ExecutionPlanInterpreterImage image)
    {
        RequireFile(image.Path, image.SizeBytes, image.Sha256, image.Identity, image.ParentIdentity);
        foreach (var file in image.RuntimeFiles)
            RequireFile(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(image.Path)!, file.RelativePath)),
                file.SizeBytes, file.Sha256, file.Identity, file.ParentIdentity);
    }

    internal ExecutionPlanInterpreterImage ReadImage(string path, string sizeError)
    {
        var input = ReadFile(path, sizeError, executable: false);
        return new() { Path = path, SizeBytes = input.Size, Sha256 = input.Sha256, Identity = input.Identity,
            ParentIdentity = input.Parent, RuntimeFiles = [] };
    }

    private void RequireFile(string path, long size, string sha256, FileTreePhysicalIdentity identity,
        FileTreePhysicalIdentity parent, bool executable = false)
    {
        var expected = new InputFile(size, sha256, identity, parent);
        if (_files.TryGetValue(path, out var previous))
        {
            if (previous != expected) throw new IOException(executable ? "execution_image_changed" : "execution_judgement_image_conflict");
            return;
        }
        var reason = executable ? "execution_image_changed" : "execution_judgement_image_changed";
        _ = ReadFile(path, reason, executable, expected);
    }

    private InputFile ReadFile(string path, string sizeError, bool executable, InputFile? expected = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_files.TryGetValue(path, out var previous)) return previous;
        if (PhysicalPathPolicy.NormalizeWithoutLinks(path) != path)
            throw new IOException(executable ? "execution_image_path_not_canonical" : "execution_judgement_path_not_canonical");
        var parent = HoldDirectory(Path.GetDirectoryName(path)!);
        if (expected is not null && parent != expected.Parent) throw new IOException("execution_directory_changed");
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var size = file.Length;
            var identity = FileTreeProtocol.ToWire(PhysicalObjectIdentity.Read(file.SafeFileHandle));
            if (expected is null ? size is < 1 or > 1073741824 : size != expected.Size || identity != expected.Identity)
                throw new IOException(sizeError);
            var snapshot = new InputFile(size, Convert.ToHexStringLower(SHA256.HashData(file)), identity, parent);
            if (expected is not null && snapshot != expected) throw new IOException(sizeError);
            _held.Add(file);
            _files.Add(path, snapshot);
            return snapshot;
        }
        catch { file.Dispose(); throw; }
    }

    internal FileTreePhysicalIdentity HoldDirectory(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_directories.TryGetValue(path, out var previous)) return previous;
        if (PhysicalPathPolicy.NormalizeWithoutLinks(path) != path) throw new IOException("execution_directory_not_canonical");
        _held.Add(StableDirectoryLease.Acquire(path));
        var identity = FileTreeProtocol.ToWire(PhysicalObjectIdentity.Read(path));
        _directories.Add(path, identity);
        return identity;
    }

    private void RequireDirectory(string path, FileTreePhysicalIdentity identity)
    {
        if (HoldDirectory(path) != identity) throw new IOException("execution_directory_changed");
    }

    public void Dispose()
    {
        _disposed = true;
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        _held.Clear();
        _files.Clear();
        _directories.Clear();
    }
}
