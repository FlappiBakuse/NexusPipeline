using System.IO.Compression;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Platform.Extensions;

internal sealed class PackagePathSet
{
    private sealed record Node(string Path, bool Directory, bool Explicit);
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase)
        { [""] = new("", true, false) };
    internal IEnumerable<string> Directories => _nodes.Values.Where(value => value.Directory).Select(value => value.Path);

    internal string Add(ZipArchiveEntry entry)
    {
        var directory = entry.FullName.EndsWith('/');
        var name = directory ? entry.FullName[..^1] : entry.FullName;
        PhysicalPathPolicy.ValidateRelativePath(name);
        var mode = (entry.ExternalAttributes >> 16) & 0xf000;
        if ((entry.ExternalAttributes & 0x400) != 0 ||
            directory && (mode is not (0 or 0x4000) || entry.Length != 0) ||
            !directory && (mode is not (0 or 0x8000) || (entry.ExternalAttributes & 0x10) != 0)) throw Invalid();
        var parts = name.Split('/'); var current = "";
        for (var index = 0; index < parts.Length; index++)
        {
            current = current.Length == 0 ? parts[index] : current + "/" + parts[index];
            var final = index == parts.Length - 1; var isDirectory = !final || directory;
            if (_nodes.TryGetValue(current, out var prior))
            {
                if (prior.Path != current || !prior.Directory || !isDirectory || final && prior.Explicit) throw Invalid();
                if (final) _nodes[current] = prior with { Explicit = true };
            }
            else
            {
                if (_nodes.Count >= ExtensionZipMetadata.MaximumEntries) throw Invalid();
                _nodes.Add(current, new(current, isDirectory, final));
            }
        }
        return name;
    }

    private static IOException Invalid() => new("extension_package_path_collision");
}
