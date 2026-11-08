using System.Collections.Immutable;

namespace NexusPipeline.Core.Configuration;

public enum TreeChangeKind { Added, Modified, Removed, Reshaped }
public sealed record FileTreeChange(string RelativePath, TreeChangeKind Kind, FileTreeEntry? Before, FileTreeEntry? After);
public sealed record FileTreeDelta(ImmutableArray<FileTreeChange> Changes, int AddedFiles, int ModifiedFiles,
    int RemovedFiles, int AddedDirectories, int RemovedDirectories)
{
    public static FileTreeDelta Compare(FileTreeSnapshot before, FileTreeSnapshot after)
    {
        var old = FileTreePolicy.Freeze(before).Entries.ToDictionary(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);
        var next = FileTreePolicy.Freeze(after).Entries.ToDictionary(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);
        var paths = old.Keys.Union(next.Keys, StringComparer.OrdinalIgnoreCase).Order(Comparer<string>.Create(FileTreePolicy.ComparePaths));
        var changes = ImmutableArray.CreateBuilder<FileTreeChange>();
        foreach (var path in paths)
        {
            old.TryGetValue(path, out var previous);
            next.TryGetValue(path, out var current);
            if (previous == current) continue;
            var kind = previous is null ? TreeChangeKind.Added : current is null ? TreeChangeKind.Removed
                : previous.Kind != current.Kind ? TreeChangeKind.Reshaped : TreeChangeKind.Modified;
            changes.Add(new(current?.RelativePath ?? path, kind, previous, current));
        }
        var frozen = changes.ToImmutable();
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
        if (elapsed < TimeSpan.Zero || !Enum.IsDefined(trigger)) throw new ConfigurationException("configuration_sync_request_invalid");
        saved = FileTreePolicy.Freeze(saved);
        candidate = FileTreePolicy.Freeze(candidate);
        if (trigger == ConfigurationSyncTrigger.InitialDetection && elapsed < TimeSpan.FromSeconds(15))
            return new(false, "configuration_sync_not_due", null);
        if (!isContentValidated) return new(false, "configuration_sync_invalid_content", null);
        var previous = saved.Entries.Where(entry => entry.Kind == FileObjectKind.File).ToArray();
        var files = candidate.Entries.Where(entry => entry.Kind == FileObjectKind.File).ToArray();
        var bytes = files.Sum(entry => entry.SizeBytes);
        if (candidate.Shape == FileObjectKind.Missing || files.Length == 0 || bytes == 0)
            return new(false, "configuration_sync_empty", null);
        var delta = FileTreeDelta.Compare(saved, candidate);
        if (delta.Changes.Any(change => change.Kind == TreeChangeKind.Reshaped))
            return new(false, "configuration_sync_shape_conflict", null);
        if (saved.Shape == FileObjectKind.Directory && previous.Length > 0 && files.Length * 2 <= previous.Length)
            return new(false, "configuration_sync_shrinking", null);
        return new(true, "configuration_sync_allowed", delta);
    }
}
