using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Platform.Execution.Judgement;

[SupportedOSPlatform("windows")]
internal sealed class JudgementInterpreterLease : IDisposable
{
    private readonly List<IDisposable> _held = [];

    internal static JudgementInterpreterImage Inspect(string requestedPath)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(requestedPath);
        var parent = Path.GetDirectoryName(path) ?? throw new IOException("judgement_interpreter_parent_missing");
        using var directory = StableDirectoryLease.Acquire(parent);
        using var image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (image.Length is < 1 or > 1073741824) throw new IOException("judgement_interpreter_size_invalid");
        var runtime = JudgementRuntimeAssets.Paths(path).Select(relative =>
        {
            var file = Path.GetFullPath(Path.Combine(parent, relative));
            using var runtimeDirectory = StableDirectoryLease.Acquire(Path.GetDirectoryName(file)!);
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is < 1 or > 1073741824) throw new IOException("judgement_runtime_asset_size_invalid");
            return new JudgementRuntimeFile(relative, input.Length, Convert.ToHexStringLower(SHA256.HashData(input)),
                PhysicalObjectIdentity.Read(input.SafeFileHandle), PhysicalObjectIdentity.Read(Path.GetDirectoryName(file)!));
        }).ToArray();
        return new(path, image.Length, Convert.ToHexStringLower(SHA256.HashData(image)),
            PhysicalObjectIdentity.Read(image.SafeFileHandle), PhysicalObjectIdentity.Read(parent), runtime);
    }

    internal JudgementInterpreterLease(JudgementWorkerInitialization initialization)
    {
        try
        {
            JudgementWorkerProtocol.Require(initialization);
            Directory(initialization.WorkingDirectory, initialization.WorkingDirectoryIdentity);
            Image(initialization.Broker);
            if (initialization.Python is { } python) Image(python);
        }
        catch { Dispose(); throw; }
    }

    private void Image(JudgementInterpreterImage expected)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(expected.Path);
        if (path != expected.Path) throw new IOException("judgement_interpreter_path_changed");
        File(path, expected.SizeBytes, expected.Sha256, expected.Identity, expected.ParentIdentity);
        foreach (var file in expected.RuntimeFiles)
            File(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, file.RelativePath)), file.SizeBytes, file.Sha256, file.Identity, file.ParentIdentity);
    }

    private void File(string path, long size, string digest, PhysicalObjectIdentity identity, PhysicalObjectIdentity parent)
    {
        if (PhysicalPathPolicy.NormalizeWithoutLinks(path) != path) throw new IOException("judgement_interpreter_path_changed");
        Directory(Path.GetDirectoryName(path)!, parent);
        var image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _held.Add(image);
        if (image.Length != size || PhysicalObjectIdentity.Read(image.SafeFileHandle) != identity ||
            Convert.ToHexStringLower(SHA256.HashData(image)) != digest)
            throw new IOException("judgement_interpreter_changed");
        image.Position = 0;
    }

    private void Directory(string path, PhysicalObjectIdentity expected)
    {
        if (PhysicalPathPolicy.NormalizeWithoutLinks(path) != path) throw new IOException("judgement_directory_not_canonical");
        _held.Add(StableDirectoryLease.Acquire(path));
        if (PhysicalObjectIdentity.Read(path) != expected) throw new IOException("judgement_directory_changed");
    }

    public void Dispose()
    {
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        _held.Clear();
    }
}
