using System.Text;

namespace NexusPipeline.Core.Configuration;

public sealed class ConfigurationException(string code) : Exception(code);

public static class FileTreePolicy
{
    public static FileTreeSnapshot Freeze(FileTreeSnapshot tree)
    {
        if (tree is null || !Enum.IsDefined(tree.Shape) || tree.Entries is null)
            throw new ConfigurationException("configuration_tree_invalid");
        var entries = tree.Entries.ToArray();
        if (entries.Length > 4096 || entries.Any(entry => entry is null) ||
            entries.Select(entry => entry.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length)
            throw new ConfigurationException("configuration_tree_invalid");
        if (tree.Shape == FileObjectKind.Missing)
        {
            if (entries.Length != 0) throw new ConfigurationException("configuration_tree_invalid");
            return new(FileObjectKind.Missing, Array.AsReadOnly(entries));
        }
        if (entries.SingleOrDefault(entry => entry.RelativePath == "")?.Kind != tree.Shape ||
            tree.Shape == FileObjectKind.File && entries.Length != 1)
            throw new ConfigurationException("configuration_tree_shape_invalid");
        long bytes = 0;
        foreach (var entry in entries)
        {
            if (!RelativePath(entry.RelativePath)) throw new ConfigurationException("configuration_tree_path_invalid");
            if (entry.Kind == FileObjectKind.File)
            {
                if (entry.SizeBytes < 0 || entry.SizeBytes > 1_073_741_824 - bytes || !Digest(entry.Sha256))
                    throw new ConfigurationException("configuration_tree_content_invalid");
                bytes += entry.SizeBytes;
            }
            else if (entry.Kind != FileObjectKind.Directory || entry.SizeBytes != 0 || entry.Sha256 is not null)
                throw new ConfigurationException("configuration_tree_entry_invalid");
            if (entry.RelativePath.Length != 0)
            {
                var separator = entry.RelativePath.LastIndexOf('/');
                var parent = separator < 0 ? "" : entry.RelativePath[..separator];
                if (!entries.Any(candidate => candidate.RelativePath == parent && candidate.Kind == FileObjectKind.Directory))
                    throw new ConfigurationException("configuration_tree_parent_missing");
            }
        }
        Array.Sort(entries, (left, right) => ComparePaths(left.RelativePath, right.RelativePath));
        return new(tree.Shape, Array.AsReadOnly(entries));
    }

    public static bool Digest(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool RelativePath(string? path) => path is not null && path.Length <= 1024 &&
        (path.Length == 0 || !path.Any(character => char.IsControl(character) || character is ':' or '\\' or '*' or '?' or '"' or '<' or '>' or '|') &&
         path.Split('/').All(part => part.Length > 0 && part is not ("." or "..") && !part.EndsWith(' ') && !part.EndsWith('.')));

    internal static int ComparePaths(string left, string right) =>
        Encoding.UTF8.GetBytes(left).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(right));
}
