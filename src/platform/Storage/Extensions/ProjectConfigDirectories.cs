using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Compilation;

namespace NexusPipeline.Platform.Storage.Extensions;

internal static class ProjectConfigDirectories
{
    internal static bool TryList(IReadOnlyList<ProjectConfigMount> mounts, string relative, out SourceDirectory? directory)
    {
        directory = null;
        var mount = Match(mounts, relative);
        if (mount is null) return false;
        var path = relative == mount.Path ? "" : relative[(mount.Path.Length + 1)..];
        var node = mount.Saved.Tree.Entries.SingleOrDefault(item => item.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (node is null) { directory = new(false, []); return true; }
        if (node.RelativePath != path || node.Kind != FileObjectKind.Directory) throw new IOException("project_configuration_shape_invalid");
        var entries = mount.Saved.Tree.Entries.Where(item => item.RelativePath != path && Parent(item.RelativePath) == path)
            .Select(item => new SourceEntry(Name(item.RelativePath), item.Kind == FileObjectKind.File ? SourceEntryKind.File : SourceEntryKind.Directory,
                Identity(mount, item.RelativePath))).OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        directory = new(true, Array.AsReadOnly(entries)); return true;
    }

    internal static SourceDirectory Merge(IReadOnlyList<ProjectConfigMount> mounts, string relative, SourceDirectory original)
    {
        var entries = original.Entries.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var mount in mounts)
        {
            var prefix = relative.Length == 0 ? "" : relative + "/";
            if (!mount.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!mount.Path.StartsWith(prefix, StringComparison.Ordinal)) throw new IOException("project_configuration_alias_changed");
            var suffix = mount.Path[prefix.Length..]; var split = suffix.IndexOf('/'); var name = split < 0 ? suffix : suffix[..split];
            var kind = split < 0 && mount.Saved.Tree.Shape == FileObjectKind.File ? SourceEntryKind.File : SourceEntryKind.Directory;
            entries[name] = new(name, kind, Identity(mount, split < 0 ? "" : suffix[..split]));
        }
        return new(original.Exists || entries.Count > 0,
            Array.AsReadOnly(entries.Values.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray()));
    }

    internal static ProjectConfigMount? Match(IReadOnlyList<ProjectConfigMount> mounts, string relative)
    {
        var mount = mounts.SingleOrDefault(item => relative.Equals(item.Path, StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith(item.Path + "/", StringComparison.OrdinalIgnoreCase));
        if (mount is not null && !relative.Equals(mount.Path, StringComparison.Ordinal) && !relative.StartsWith(mount.Path + "/", StringComparison.Ordinal))
            throw new IOException("project_configuration_alias_changed");
        return mount;
    }

    internal static string Identity(ProjectConfigMount mount, string relative) => "configuration:" + mount.Saved.Address.BindingId.ToString("D") +
        "/" + mount.Saved.Address.ResourceId + "/" + mount.Saved.Revision + "/" + mount.Manifest.Sha256 + "/" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(relative)));
    private static string Parent(string path) => path.LastIndexOf('/') is var split && split >= 0 ? path[..split] : "";
    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];
}
