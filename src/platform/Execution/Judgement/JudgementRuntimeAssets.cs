using System.Runtime.InteropServices;
using System.Text.Json;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;

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
