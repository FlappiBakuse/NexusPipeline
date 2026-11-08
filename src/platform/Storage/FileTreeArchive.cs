using System.Buffers;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage;

public sealed record ArchivedFileTree(FileTreeSnapshot Snapshot, IReadOnlyList<PhysicalTreeObject> OriginalObjects,
    IReadOnlyList<BlobReference> Contents)
{
    public PhysicalObjectIdentity? OriginalIdentity => OriginalObjects.SingleOrDefault(item => item.RelativePath.Length == 0)?.Identity;
}

public sealed class FileTreeArchive(FileTreeScanner scanner, ImmutableBlobStore blobs)
{
    public ArchivedFileTree Capture(string requestedSource, CancellationToken cancellationToken)
        => CaptureCore(requestedSource, cancellationToken, scanner.CaptureArchive);

    internal ArchivedFileTree CaptureApplication(string requestedSource, CancellationToken cancellationToken)
        => CaptureCore(requestedSource, cancellationToken, (path, cancellation) => ApplicationTree.Capture(scanner, path, cancellation));

    private ArchivedFileTree CaptureCore(string requestedSource, CancellationToken cancellationToken,
        Func<string, CancellationToken, FileTreeSnapshot> capture)
    {
        var source = PhysicalPathPolicy.NormalizeWithoutLinks(requestedSource);
        var snapshot = capture(source, cancellationToken);
        var identities = PhysicalTreeIdentity.Capture(source, snapshot);
        var contents = new List<BlobReference>();
        foreach (var entry in snapshot.Entries.Where(entry => entry.Kind == FileObjectKind.File))
        {
            var file = entry.RelativePath == "" ? source : Path.Combine(source, entry.RelativePath);
            contents.Add(blobs.PersistContent(file, entry, cancellationToken));
        }
        var rechecked = capture(source, cancellationToken);
        if (snapshot.Shape != rechecked.Shape || !snapshot.Entries.SequenceEqual(rechecked.Entries) ||
            !identities.SequenceEqual(PhysicalTreeIdentity.Capture(source, rechecked)))
            throw new IOException("file_tree_archive_source_changed");
        return new(snapshot, identities, Array.AsReadOnly(contents.Distinct().ToArray()));
    }

    public PhysicalObjectIdentity? MaterializeNew(ArchivedFileTree archive, string requestedDestination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        archive = archive with { Snapshot = archive.Snapshot with { Entries = Array.AsReadOnly(archive.Snapshot.Entries.ToArray()) },
            Contents = Array.AsReadOnly(archive.Contents.ToArray()) };
        var destination = PhysicalPathPolicy.NormalizeWithoutLinks(requestedDestination);
        var before = scanner.Capture(destination, cancellationToken);
        if (before.Shape != FileObjectKind.Missing) throw new IOException("file_tree_destination_not_empty");
        ValidateArchive(archive);
        var contents = archive.Contents.ToDictionary(blob => blob.Sha256, StringComparer.Ordinal);
        var held = new Dictionary<BlobReference, FileStream>();
        try
        {
            foreach (var blob in archive.Contents) held.Add(blob, blobs.OpenVerified(blob));
            if (archive.Snapshot.Shape == FileObjectKind.Missing) return null;
            if (archive.Snapshot.Shape == FileObjectKind.Directory) ExclusiveDirectory.Create(destination);
            foreach (var entry in archive.Snapshot.Entries.OrderBy(entry => entry.RelativePath.Count(character => character == '/')))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.RelativePath.Length == 0 && entry.Kind == FileObjectKind.Directory) continue;
                var path = entry.RelativePath.Length == 0 ? destination : Path.Combine(destination, entry.RelativePath);
                PhysicalPathPolicy.NormalizeWithoutLinks(path);
                if (entry.Kind == FileObjectKind.Directory) ExclusiveDirectory.Create(path);
                else WriteNewFile(path, held[contents[entry.Sha256!]], cancellationToken);
            }
            var actual = scanner.Capture(destination, cancellationToken);
            if (actual.Shape != archive.Snapshot.Shape || !actual.Entries.SequenceEqual(archive.Snapshot.Entries))
                throw new IOException("file_tree_materialization_mismatch");
            return PhysicalObjectIdentity.Read(destination);
        }
        finally { foreach (var input in held.Values) input.Dispose(); }
    }

    internal static void ValidateArchive(ArchivedFileTree archive)
    {
        var snapshot = archive.Snapshot;
        var entries = new Dictionary<string, FileTreeEntry>(StringComparer.OrdinalIgnoreCase);
        var contents = new Dictionary<string, BlobReference>(StringComparer.Ordinal);
        if (!Enum.IsDefined(snapshot.Shape) || snapshot.Entries.Count > 4096 || archive.Contents.Count > 4096 ||
            snapshot.Entries.Any(entry => !entries.TryAdd(entry.RelativePath, entry)) ||
            archive.Contents.Any(blob => !contents.TryAdd(blob.Sha256, blob)))
            throw new IOException("file_tree_archive_invalid");
        if (snapshot.Shape == FileObjectKind.Missing)
        {
            if (snapshot.Entries.Count != 0 || archive.Contents.Count != 0) throw new IOException("file_tree_archive_invalid");
            return;
        }
        var root = entries.GetValueOrDefault("");
        if (root?.Kind != snapshot.Shape || snapshot.Shape == FileObjectKind.File && snapshot.Entries.Count != 1)
            throw new IOException("file_tree_archive_shape_invalid");
        long total = 0;
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in snapshot.Entries)
        {
            if (entry.RelativePath.Length != 0) PhysicalPathPolicy.ValidateRelativePath(entry.RelativePath);
            if (entry.Kind == FileObjectKind.Directory)
            {
                if (entry.SizeBytes != 0 || entry.Sha256 is not null) throw new IOException("file_tree_archive_directory_invalid");
            }
            else if (entry.Kind == FileObjectKind.File)
            {
                total = checked(total + entry.SizeBytes);
                if (entry.SizeBytes < 0 || total > 1_073_741_824 ||
                    entry.Sha256 is null || !contents.TryGetValue(entry.Sha256, out var blob) || blob.SizeBytes != entry.SizeBytes)
                    throw new IOException("file_tree_archive_content_invalid");
                referenced.Add(entry.Sha256!);
            }
            else throw new IOException("file_tree_archive_entry_invalid");
            if (entry.RelativePath.Length != 0)
            {
                var separator = entry.RelativePath.LastIndexOf('/');
                var parent = separator < 0 ? "" : entry.RelativePath[..separator];
                if (!entries.TryGetValue(parent, out var directory) || directory.RelativePath != parent || directory.Kind != FileObjectKind.Directory)
                    throw new IOException("file_tree_archive_parent_missing");
            }
        }
        if (contents.Keys.Any(digest => !referenced.Contains(digest)))
            throw new IOException("file_tree_archive_unreferenced_content");
    }

    private static void WriteNewFile(string destination, FileStream source, CancellationToken cancellationToken)
    {
        source.Position = 0;
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int count;
            while ((count = source.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, count);
            }
            output.Flush(flushToDisk: true);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
