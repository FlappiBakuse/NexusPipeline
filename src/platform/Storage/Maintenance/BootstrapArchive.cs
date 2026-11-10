using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal static class BootstrapArchive
{
    internal static ArchivedFileTree Select(ArchivedFileTree candidate, string entryPath)
    {
        FileTreeArchive.ValidateArchive(candidate);
        PhysicalPathPolicy.ValidateRelativePath(entryPath);
        if (!candidate.Snapshot.Entries.Any(entry => entry.RelativePath == entryPath && entry.Kind == FileObjectKind.File))
            throw new IOException("maintenance_bootstrap_entry_missing");
        var entries = candidate.Snapshot.Entries.Where(entry => entry.RelativePath.Length == 0 ||
            entry.RelativePath == entryPath || entry.Kind == FileObjectKind.Directory &&
            entryPath.StartsWith(entry.RelativePath + "/", StringComparison.Ordinal)).ToArray();
        var paths = entries.Select(entry => entry.RelativePath).ToHashSet(StringComparer.Ordinal);
        var hashes = entries.Where(entry => entry.Kind == FileObjectKind.File).Select(entry => entry.Sha256).ToHashSet(StringComparer.Ordinal);
        var result = new ArchivedFileTree(new(candidate.Snapshot.Shape, Array.AsReadOnly(entries)),
            Array.AsReadOnly(candidate.OriginalObjects.Where(item => paths.Contains(item.RelativePath)).ToArray()),
            Array.AsReadOnly(candidate.Contents.Where(blob => hashes.Contains(blob.Sha256)).ToArray()));
        FileTreeArchive.ValidateArchive(result);
        return result;
    }
}
