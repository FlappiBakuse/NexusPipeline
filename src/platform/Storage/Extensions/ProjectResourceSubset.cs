using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class ProjectResourceSubset : IDisposable
{
    private sealed record Part(string Relative, string Path, FileTreeSnapshot Before, IReadOnlyList<PhysicalTreeObject> Identities,
        IReadOnlyList<ResourceClaim> Claims);
    private readonly List<IDisposable> _held = [];
    private readonly Dictionary<string, PhysicalObjectIdentity> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TreeReadLease> _trees = [];
    internal IReadOnlyList<string> IncludedPaths { get; private set; } = [];
    internal ArchivedFileTree Archive { get; private set; } = null!;

    internal static ProjectResourceSubset Capture(string root, IReadOnlyList<string> included, ResourceLeaseManager manager,
        ResourceOperationLease? actualRoot, ImmutableBlobStore blobs, CancellationToken cancellation)
    {
        var result = new ProjectResourceSubset();
        ProjectSubsetPolicy.Require("", included);
        try { result.CaptureCore(root, included, manager, actualRoot, blobs, cancellation); return result; }
        catch { result.Dispose(); throw; }
    }

    private void CaptureCore(string root, IReadOnlyList<string> included, ResourceLeaseManager manager,
        ResourceOperationLease? actualRoot, ImmutableBlobStore blobs, CancellationToken cancellation)
    {
        root = PhysicalPathPolicy.NormalizeWithoutLinks(root);
        if (!Directory.Exists(root)) throw Changed();
        IncludedPaths = included.Order(StringComparer.Ordinal).ToArray();
        var entries = new Dictionary<string, FileTreeEntry>(StringComparer.OrdinalIgnoreCase);
        var identities = new Dictionary<string, PhysicalTreeObject>(StringComparer.OrdinalIgnoreCase);
        void DirectoryEntry(string relative)
        {
            var path = relative.Length == 0 ? root : Path.Combine(root, relative);
            path = PhysicalPathPolicy.NormalizeWithoutLinks(path);
            if (_directories.ContainsKey(path)) return;
            if (entries.Count >= 4096) throw new IOException("project_resource_subset_limit");
            _held.Add(StableDirectoryLease.Acquire(path)); var identity = PhysicalObjectIdentity.Read(path); _directories.Add(path, identity);
            entries.Add(relative, new(relative, FileObjectKind.Directory, 0, null)); identities.Add(relative, new(relative, identity));
        }
        DirectoryEntry(""); var scanner = new FileTreeScanner(SnapshotBudget.Default); var parts = new List<Part>();
        foreach (var relative in included.Order(StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(root, relative));
            var before = scanner.CaptureArchive(path, cancellation);
            if (before.Shape == FileObjectKind.Missing) throw Changed();
            if (parts.Sum(part => part.Before.Entries.Count) + before.Entries.Count + entries.Count > 4096 ||
                parts.Sum(part => part.Before.Entries.Sum(entry => entry.SizeBytes)) + before.Entries.Sum(entry => entry.SizeBytes) > 1_073_741_824)
                throw new IOException("project_resource_subset_limit");
            var parent = relative.LastIndexOf('/');
            for (var offset = relative.IndexOf('/'); offset >= 0 && offset <= parent; offset = relative.IndexOf('/', offset + 1)) DirectoryEntry(relative[..offset]);
            parts.Add(new(relative, path, before, PhysicalTreeIdentity.Capture(path, before), PhysicalResourceAdmission.ReadTree(path, before)));
        }
        if (parts.Sum(part => part.Before.Entries.Count) + entries.Count > 4096) throw new IOException("project_resource_subset_limit");
        var claims = PhysicalResourceAdmission.Merge(parts.SelectMany(part => part.Claims));
        foreach (var batch in claims.Chunk(4096)) _held.Add(ProjectReadLease.Acquire(manager, batch, actualRoot));
        var contents = new List<BlobReference>();
        foreach (var part in parts)
        {
            var archive = new FileTreeArchive(scanner, blobs).Capture(part.Path, cancellation);
            if (archive.Snapshot.Shape != part.Before.Shape || !archive.Snapshot.Entries.SequenceEqual(part.Before.Entries) ||
                !archive.OriginalObjects.SequenceEqual(part.Identities) || !PhysicalResourceAdmission.ReadTree(part.Path, archive.Snapshot).SequenceEqual(part.Claims)) throw Changed();
            var tree = TreeReadLease.Acquire(part.Path, PhysicalObjectIdentity.Read(Path.GetDirectoryName(part.Path)!), archive);
            _held.Add(tree); _trees.Add(tree);
            contents.AddRange(archive.Contents);
            foreach (var entry in archive.Snapshot.Entries)
            {
                var relative = entry.RelativePath.Length == 0 ? part.Relative : part.Relative + "/" + entry.RelativePath;
                if (!entries.TryAdd(relative, entry with { RelativePath = relative }) || !identities.TryAdd(relative,
                    new(relative, archive.OriginalObjects.Single(value => value.RelativePath == entry.RelativePath).Identity))) throw Changed();
            }
        }
        Archive = new(new(FileObjectKind.Directory, entries.Values.OrderBy(value => value.RelativePath, StringComparer.Ordinal).ToArray()),
            identities.Values.OrderBy(value => value.RelativePath, StringComparer.Ordinal).ToArray(), contents.Distinct().ToArray());
        FileTreeArchive.ValidateArchive(Archive); ProjectSubsetPolicy.RequireClosure(Archive, included); RequireCurrent();
        foreach (var content in Archive.Contents) { cancellation.ThrowIfCancellationRequested(); _held.Add(blobs.OpenVerified(content)); }
    }

    internal void RequireCurrent()
    {
        foreach (var (path, identity) in _directories) if (PhysicalObjectIdentity.Read(path) != identity) throw Changed();
        foreach (var tree in _trees) { using var current = tree.Borrow(); }
    }
    public void Dispose() { for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose(); _held.Clear(); }
    private static IOException Changed() => new("project_source_changed");
}
