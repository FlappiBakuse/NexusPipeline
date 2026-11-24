using System.Collections.Immutable;
using System.Text;

namespace NexusPipeline.Core.Configuration;

public enum FileObjectKind { Missing, File, Directory }

public sealed record FileTreeEntry(string RelativePath, FileObjectKind Kind, long SizeBytes, string? Sha256);

public sealed record FileTreeSnapshot(FileObjectKind Shape, IReadOnlyList<FileTreeEntry> Entries);

public sealed class ConfigurationException(string code) : Exception(code);

public static class FileTreePolicy
{
    internal static readonly IComparer<byte[]> PathKeys = Comparer<byte[]>.Create((left, right) =>
        left.AsSpan().SequenceCompareTo(right));

    public static FileTreeSnapshot Freeze(FileTreeSnapshot tree) => Prepare(tree).Snapshot;

    internal static PreparedTree Prepare(FileTreeSnapshot tree)
    {
        if (tree is null || !Enum.IsDefined(tree.Shape) || tree.Entries is null)
            throw new ConfigurationException("configuration_tree_invalid");
        var entries = tree.Entries.ToArray();
        if (entries.Length > 4096) throw new ConfigurationException("configuration_tree_invalid");
        var paths = new Dictionary<string, FileTreeEntry>(entries.Length, StringComparer.OrdinalIgnoreCase);
        if (tree.Shape == FileObjectKind.Missing)
        {
            if (entries.Length != 0) throw new ConfigurationException("configuration_tree_invalid");
            return new(new(FileObjectKind.Missing, Array.AsReadOnly(entries)), paths, [], 0, 0);
        }
        var hasNullPath = false;
        foreach (var entry in entries)
        {
            if (entry is null) throw new ConfigurationException("configuration_tree_invalid");
            if (entry.RelativePath is { } path)
            {
                if (!paths.TryAdd(path, entry)) throw new ConfigurationException("configuration_tree_invalid");
            }
            else if (hasNullPath) throw new ConfigurationException("configuration_tree_invalid");
            else hasNullPath = true;
        }
        if (paths.GetValueOrDefault("")?.Kind != tree.Shape ||
            tree.Shape == FileObjectKind.File && entries.Length != 1)
            throw new ConfigurationException("configuration_tree_shape_invalid");

        var keys = new byte[entries.Length][];
        var files = 0;
        long bytes = 0;
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (!RelativePath(entry.RelativePath)) throw new ConfigurationException("configuration_tree_path_invalid");
            if (entry.Kind == FileObjectKind.File)
            {
                if (entry.SizeBytes < 0 || entry.SizeBytes > 1_073_741_824 - bytes || !Digest(entry.Sha256))
                    throw new ConfigurationException("configuration_tree_content_invalid");
                bytes += entry.SizeBytes;
                files++;
            }
            else if (entry.Kind != FileObjectKind.Directory || entry.SizeBytes != 0 || entry.Sha256 is not null)
                throw new ConfigurationException("configuration_tree_entry_invalid");
            if (entry.RelativePath.Length != 0)
            {
                var separator = entry.RelativePath.LastIndexOf('/');
                var parent = separator < 0 ? "" : entry.RelativePath[..separator];
                if (!paths.TryGetValue(parent, out var directory) || directory.RelativePath != parent || directory.Kind != FileObjectKind.Directory)
                    throw new ConfigurationException("configuration_tree_parent_missing");
            }
            keys[index] = Encoding.UTF8.GetBytes(entry.RelativePath);
        }
        Array.Sort(keys, entries, PathKeys);
        return new(new(tree.Shape, Array.AsReadOnly(entries)), paths, keys, files, bytes);
    }

    internal sealed class PreparedTree(FileTreeSnapshot snapshot, Dictionary<string, FileTreeEntry> paths,
        byte[][] keys, int files, long bytes)
    {
        internal FileTreeSnapshot Snapshot => snapshot;
        internal int FileCount => files;
        internal long FileBytes => bytes;
        internal bool Contains(string path) => paths.ContainsKey(path);
        internal FileTreeEntry? Get(string path) => paths.GetValueOrDefault(path);
        internal IEnumerable<(string Path, byte[] Key)> Paths()
        {
            for (var index = 0; index < keys.Length; index++)
                yield return (snapshot.Entries[index].RelativePath, keys[index]);
        }
    }

    public static bool Digest(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool RelativePath(string? path) => path is not null && path.Length <= 1024 &&
        (path.Length == 0 || !path.Any(character => char.IsControl(character) || character is ':' or '\\' or '*' or '?' or '"' or '<' or '>' or '|') &&
         path.Split('/').All(part => part.Length > 0 && part is not ("." or "..") && !part.EndsWith(' ') && !part.EndsWith('.')));

    internal static int ComparePaths(string left, string right) =>
        Encoding.UTF8.GetBytes(left).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(right));
}

public enum TreeChangeKind { Added, Modified, Removed, Reshaped }
public sealed record FileTreeChange(string RelativePath, TreeChangeKind Kind, FileTreeEntry? Before, FileTreeEntry? After);
public sealed record FileTreeDelta(ImmutableArray<FileTreeChange> Changes, int AddedFiles, int ModifiedFiles,
    int RemovedFiles, int AddedDirectories, int RemovedDirectories)
{
    public static FileTreeDelta Compare(FileTreeSnapshot before, FileTreeSnapshot after)
        => Compare(FileTreePolicy.Prepare(before), FileTreePolicy.Prepare(after));

    internal static FileTreeDelta Compare(FileTreePolicy.PreparedTree before, FileTreePolicy.PreparedTree after)
    {
        var paths = before.Paths().Concat(after.Paths().Where(item => !before.Contains(item.Path)));
        var changes = new List<(FileTreeChange Change, byte[] Key)>();
        foreach (var (path, key) in paths)
        {
            var previous = before.Get(path);
            var current = after.Get(path);
            if (previous == current) continue;
            var kind = previous is null ? TreeChangeKind.Added : current is null ? TreeChangeKind.Removed
                : previous.Kind != current.Kind ? TreeChangeKind.Reshaped : TreeChangeKind.Modified;
            changes.Add((new(current?.RelativePath ?? path, kind, previous, current), key));
        }
        var frozen = changes.OrderBy(item => item.Key, FileTreePolicy.PathKeys).Select(item => item.Change).ToImmutableArray();
        return new(frozen,
            frozen.Count(change => change.After?.Kind == FileObjectKind.File && change.Before?.Kind != FileObjectKind.File),
            frozen.Count(change => change.After?.Kind == FileObjectKind.File && change.Before?.Kind == FileObjectKind.File),
            frozen.Count(change => change.Before?.Kind == FileObjectKind.File && change.After?.Kind != FileObjectKind.File),
            frozen.Count(change => change.After?.Kind == FileObjectKind.Directory && change.Before?.Kind != FileObjectKind.Directory),
            frozen.Count(change => change.Before?.Kind == FileObjectKind.Directory && change.After?.Kind != FileObjectKind.Directory));
    }
}

public enum ConfigurationSyncTrigger { InitialDetection, Finalization }
public sealed record ConfigurationSyncDecision(bool IsAllowed, string ReasonCode, FileTreeDelta? Delta);

public static class ConfigurationSynchronizationPolicy
{
    public static ConfigurationSyncDecision Evaluate(FileTreeSnapshot saved, FileTreeSnapshot candidate,
        TimeSpan elapsed, bool isContentValidated, ConfigurationSyncTrigger trigger = ConfigurationSyncTrigger.InitialDetection)
    {
        RequireTrigger(elapsed, trigger);
        return Evaluate(FileTreePolicy.Prepare(saved), FileTreePolicy.Prepare(candidate), elapsed, isContentValidated, trigger);
    }

    public static ConfigurationSyncDecision Evaluate(FileTreeSnapshot baseline, FileTreeSnapshot saved,
        FileTreeSnapshot candidate, TimeSpan elapsed, bool isContentValidated, ConfigurationSyncTrigger trigger)
    {
        RequireTrigger(elapsed, trigger);
        var original = FileTreePolicy.Prepare(baseline);
        var incoming = FileTreePolicy.Prepare(candidate);
        var baselineDecision = Evaluate(original, incoming, elapsed, isContentValidated, trigger);
        var savedDecision = Evaluate(FileTreePolicy.Prepare(saved), incoming, elapsed, isContentValidated, trigger);
        return baselineDecision.IsAllowed ? savedDecision : baselineDecision;
    }

    private static ConfigurationSyncDecision Evaluate(FileTreePolicy.PreparedTree saved, FileTreePolicy.PreparedTree candidate,
        TimeSpan elapsed, bool isContentValidated, ConfigurationSyncTrigger trigger)
    {
        if (trigger == ConfigurationSyncTrigger.InitialDetection && elapsed < TimeSpan.FromSeconds(15))
            return new(false, "configuration_sync_not_due", null);
        if (!isContentValidated) return new(false, "configuration_sync_invalid_content", null);
        if (candidate.FileCount == 0 || candidate.FileBytes == 0)
            return new(false, "configuration_sync_empty", null);
        var delta = FileTreeDelta.Compare(saved, candidate);
        if (delta.Changes.Any(change => change.Kind == TreeChangeKind.Reshaped))
            return new(false, "configuration_sync_shape_conflict", null);
        if (saved.Snapshot.Shape == FileObjectKind.Directory && saved.FileCount > 0 && candidate.FileCount * 2 <= saved.FileCount)
            return new(false, "configuration_sync_shrinking", null);
        return new(true, "configuration_sync_allowed", delta);
    }

    private static void RequireTrigger(TimeSpan elapsed, ConfigurationSyncTrigger trigger)
    {
        if (elapsed < TimeSpan.Zero || !Enum.IsDefined(trigger)) throw new ConfigurationException("configuration_sync_request_invalid");
    }
}
