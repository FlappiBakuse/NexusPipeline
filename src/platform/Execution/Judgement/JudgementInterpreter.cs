using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace NexusPipeline.Platform.Execution.Judgement;

internal static class JudgementRuntimeAssets
{
    internal const int MaximumFiles = 512;

    internal static IReadOnlyList<string> Paths(string executable)
    {
        var directory = Path.GetDirectoryName(executable)!;
        var stem = Path.GetFileNameWithoutExtension(executable);
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        void Add(string relative)
        {
            PhysicalPathPolicy.ValidateRelativePath(relative);
            if (!File.Exists(Path.Combine(directory, relative))) throw new IOException("judgement_runtime_asset_missing");
            paths.Add(relative);
            if (paths.Count > MaximumFiles) throw new IOException("judgement_runtime_asset_limit");
        }
        var deps = Path.Combine(directory, stem + ".deps.json");
        if (File.Exists(deps))
        {
            Add(stem + ".deps.json"); Add(stem + ".runtimeconfig.json"); Add(stem + ".dll");
            using var stream = new FileStream(deps, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 or > StrictJson.MaximumBytes) throw new IOException("judgement_runtime_manifest_invalid");
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            using var document = StrictJson.Parse(bytes);
            var root = document.RootElement;
            var target = root.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
            foreach (var library in root.GetProperty("targets").GetProperty(target).EnumerateObject())
            {
                if (library.Value.TryGetProperty("runtime", out var runtime))
                    foreach (var asset in runtime.EnumerateObject()) Add(Resolve(directory, asset.Name));
                if (library.Value.TryGetProperty("native", out var native))
                    foreach (var asset in native.EnumerateObject()) Add(Resolve(directory, asset.Name));
                if (!library.Value.TryGetProperty("runtimeTargets", out var targets)) continue;
                foreach (var asset in targets.EnumerateObject())
                    if (asset.Value.GetProperty("rid").GetString() == RuntimeInformation.RuntimeIdentifier)
                        Add(Resolve(directory, asset.Name));
            }
        }
        else
        {
            // Python's adjacent loader assets are mutable interpreter inputs; OS and shared framework servicing are external provenance.
            foreach (var path in System.IO.Directory.EnumerateFiles(directory, "python*.dll", SearchOption.TopDirectoryOnly)) Add(Path.GetFileName(path));
            foreach (var path in System.IO.Directory.EnumerateFiles(directory, "python*.zip", SearchOption.TopDirectoryOnly)) Add(Path.GetFileName(path));
            foreach (var name in new[] { stem + "._pth", "pyvenv.cfg" })
                if (File.Exists(Path.Combine(directory, name))) Add(name);
        }
        paths.Remove(Path.GetFileName(executable));
        return paths.ToArray();
    }

    private static string Resolve(string directory, string asset)
    {
        var relative = asset;
        PhysicalPathPolicy.ValidateRelativePath(relative);
        var flat = Path.GetFileName(relative);
        return File.Exists(Path.Combine(directory, flat)) ? flat : relative;
    }
}

public static class JudgementRuntimeDiscovery
{
    public static string? FindPython()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = directory.Trim('"');
            if (!Path.IsPathFullyQualified(path)) continue;
            var candidate = Path.Combine(path, "python.exe");
            if (!File.Exists(candidate)) continue;
            try { return Storage.PhysicalPathPolicy.NormalizeWithoutLinks(candidate); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return null;
    }
}

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
