using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage;

internal static class ApplicationTree
{
    internal const string HostEntry = "NexusPipeline.exe";
    internal const string LauncherEntry = "resources/desktop/NexusPipeline.Launcher.exe";
    internal const string MaintenanceEntry = "NexusPipeline.update.exe";
    internal const string DesktopEntry = "resources/desktop/NexusPipeline.Desktop.exe";
    internal const string DesktopArchive = "resources/desktop/resources/app.asar";
    internal const string ManifestPath = "resources/payload-manifest.json";
    internal static ImmutableArray<string> Roots { get; } =
        [HostEntry, MaintenanceEntry, "README.md", "resources"];

    internal static FileTreeSnapshot Capture(FileTreeScanner scanner, string requestedRoot, CancellationToken cancellation)
    {
        var root = PhysicalPathPolicy.NormalizeWithoutLinks(requestedRoot);
        if ((File.GetAttributes(root) & FileAttributes.Directory) == 0) throw Changed();
        var entries = new List<FileTreeEntry> { new("", FileObjectKind.Directory, 0, null) };
        foreach (var name in Roots)
        {
            var child = scanner.Capture(Path.Combine(root, name), cancellation);
            foreach (var entry in child.Entries)
                entries.Add(entry with { RelativePath = entry.RelativePath.Length == 0 ? name : name + "/" + entry.RelativePath });
        }
        var result = new FileTreeSnapshot(FileObjectKind.Directory, Array.AsReadOnly(entries.OrderBy(entry => entry.RelativePath,
            Comparer<string>.Create((left, right) => System.Text.Encoding.UTF8.GetBytes(left).AsSpan()
                .SequenceCompareTo(System.Text.Encoding.UTF8.GetBytes(right)))).ToArray()));
        Validate(result, complete: false);
        return result;
    }

    internal static void Validate(FileTreeSnapshot snapshot, bool complete)
    {
        if (snapshot.Shape != FileObjectKind.Directory || snapshot.Entries.Count > 4096 ||
            snapshot.Entries.Where(entry => entry.Kind == FileObjectKind.File).Sum(entry => entry.SizeBytes) > 1073741824 ||
            snapshot.Entries.Any(entry => entry.RelativePath.Length != 0 && !Roots.Contains(entry.RelativePath.Split('/')[0], StringComparer.Ordinal)))
            throw Changed();
        foreach (var name in Roots)
        {
            var entry = snapshot.Entries.SingleOrDefault(entry => entry.RelativePath == name);
            if (entry is null ? complete : entry.Kind != (name == "resources" ? FileObjectKind.Directory : FileObjectKind.File))
                throw Changed();
        }
    }

    internal static ArchivedFileTree Slice(ArchivedFileTree tree, string name, IReadOnlyList<PhysicalTreeObject>? objects = null)
    {
        if (!Roots.Contains(name, StringComparer.Ordinal)) throw Changed();
        var root = tree.Snapshot.Entries.SingleOrDefault(entry => entry.RelativePath == name);
        if (root is null) return new(new(FileObjectKind.Missing, []), [], []);
        bool Inside(string path) => path == name || path.StartsWith(name + "/", StringComparison.Ordinal);
        string Relative(string path) => path.Length == name.Length ? "" : path[(name.Length + 1)..];
        var entries = tree.Snapshot.Entries.Where(entry => Inside(entry.RelativePath))
            .Select(entry => entry with { RelativePath = Relative(entry.RelativePath) }).ToArray();
        var identities = (objects ?? tree.OriginalObjects).Where(item => Inside(item.RelativePath))
            .Select(item => item with { RelativePath = Relative(item.RelativePath) }).ToArray();
        var contents = tree.Contents.Where(blob => entries.Any(entry => entry.Kind == FileObjectKind.File && entry.Sha256 == blob.Sha256)).ToArray();
        var result = new ArchivedFileTree(new(root.Kind, Array.AsReadOnly(entries)), Array.AsReadOnly(identities), Array.AsReadOnly(contents));
        FileTreeArchive.ValidateArchive(result);
        return result;
    }

    internal static ArchivedFileTree Installed(FrozenPayloadApply plan, IReadOnlyList<PhysicalTreeObject> objects)
    {
        Validate(plan.Candidate.Snapshot, complete: true);
        var identity = plan.Before.OriginalIdentity ?? throw Changed();
        return plan.Candidate with { OriginalObjects = Array.AsReadOnly(objects.Select(item => item.RelativePath.Length == 0
            ? item with { Identity = identity } : item).ToArray()) };
    }

    internal static IOException Changed() => new("maintenance_payload_tree_changed");
}
