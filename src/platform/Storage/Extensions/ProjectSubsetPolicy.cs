using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Extensions;

internal static class ProjectSubsetPolicy
{
    internal static void Require(string? path, IReadOnlyList<string>? included)
    {
        if (path is null) throw Invalid();
        if (path.Length != 0) PhysicalPathPolicy.ValidateRelativePath(path);
        if (included is null) { if (path.Length == 0) throw Invalid(); return; }
        if (included.Count is < 1 or > 128 || included.Any(string.IsNullOrEmpty) ||
            included.Distinct(StringComparer.OrdinalIgnoreCase).Count() != included.Count) throw Invalid();
        foreach (var value in included)
        {
            PhysicalPathPolicy.ValidateRelativePath(value); PhysicalPathPolicy.ValidateRelativePath(Combine(path, value));
            if (included.Any(other => value.StartsWith(other + "/", StringComparison.OrdinalIgnoreCase))) throw Invalid();
        }
    }

    internal static string Combine(string parent, string path) => parent.Length == 0 ? path : parent + "/" + path;

    internal static void RequireClosure(ArchivedFileTree archive, IReadOnlyList<string> included)
    {
        if (archive.Snapshot.Shape != FileObjectKind.Directory || archive.OriginalObjects.Count != archive.Snapshot.Entries.Count ||
            archive.OriginalObjects.Select(value => value.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != archive.OriginalObjects.Count ||
            archive.OriginalObjects.Any(value => !archive.Snapshot.Entries.Any(entry => entry.RelativePath == value.RelativePath)) ||
            included.Any(path => !archive.Snapshot.Entries.Any(entry => entry.RelativePath == path))) throw Invalid();
        foreach (var entry in archive.Snapshot.Entries.Where(value => value.RelativePath.Length != 0))
            if (!included.Any(path => entry.RelativePath == path || entry.RelativePath.StartsWith(path + "/", StringComparison.Ordinal) ||
                entry.Kind == FileObjectKind.Directory && path.StartsWith(entry.RelativePath + "/", StringComparison.Ordinal))) throw Invalid();
    }

    private static IOException Invalid() => new("project_resource_subset_invalid");
}
