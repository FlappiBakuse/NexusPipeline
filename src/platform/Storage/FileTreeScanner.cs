using System.Security.Cryptography;
using System.Text;
using System.Buffers;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage;

public sealed record SnapshotBudget(int MaximumEntries, long MaximumTotalBytes)
{
    public static SnapshotBudget Default { get; } = new(4096, 1_073_741_824);
}

public sealed class FileTreeScanner(SnapshotBudget budget)
{
    private const int BufferedCaptureBytes = 65536;

    public FileTreeSnapshot Capture(string selectedRoot, CancellationToken cancellationToken)
    {
        RequireBudget(cancellationToken);
        var root = PhysicalPathPolicy.NormalizeWithoutLinks(selectedRoot);
        var first = Scan(root, cancellationToken);
        var second = Scan(root, cancellationToken);
        if (first.Shape != second.Shape || !first.Entries.SequenceEqual(second.Entries))
            throw new IOException("snapshot_source_changed");
        return first;
    }

    internal FileTreeSnapshot CaptureArchive(string selectedRoot, CancellationToken cancellationToken)
    {
        RequireBudget(cancellationToken);
        return Scan(PhysicalPathPolicy.NormalizeWithoutLinks(selectedRoot), cancellationToken);
    }

    internal FileTreeSnapshot CaptureProtected(string root, TreeReadLease.CaptureSources sources, CancellationToken cancellationToken)
    {
        RequireBudget(cancellationToken);
        return Scan(root, cancellationToken, sources);
    }

    private void RequireBudget(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (budget.MaximumEntries < 1 || budget.MaximumTotalBytes < 0)
            throw new ArgumentException("Invalid snapshot budget");
    }

    private FileTreeSnapshot Scan(string root, CancellationToken cancellationToken, TreeReadLease.CaptureSources? sources = null)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(root); }
        catch (FileNotFoundException) { return new(FileObjectKind.Missing, []); }
        catch (DirectoryNotFoundException) { return new(FileObjectKind.Missing, []); }
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("snapshot_link_rejected");
        var result = new List<FileTreeEntry>();
        long total = 0;
        var shape = (attributes & FileAttributes.Directory) != 0 ? FileObjectKind.Directory : FileObjectKind.File;
        ReadEntry(root, root, "", attributes, result, ref total, cancellationToken, sources);
        if (result.Select(entry => entry.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
            throw new IOException("snapshot_path_collision");
        return new(shape, Array.AsReadOnly(result.OrderBy(entry => Encoding.UTF8.GetBytes(entry.RelativePath), Utf8PathComparer.Instance).ToArray()));
    }

    private void ReadEntry(string root, string path, string relative, FileAttributes attributes,
        List<FileTreeEntry> result, ref long total, CancellationToken cancellationToken, TreeReadLease.CaptureSources? sources)
    {
        if ((attributes & FileAttributes.Directory) == 0)
        {
            using var temporary = sources is null ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
            var source = temporary ?? sources!.OpenFile(path, relative);
            result.Add(ReadFile(source, relative, ref total, cancellationToken, sources));
            return;
        }
        sources?.HoldDirectory(path, relative);
        result.Add(new(relative, FileObjectKind.Directory, 0, null));
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Count >= budget.MaximumEntries) throw new IOException("snapshot_entry_limit");
            PhysicalPathPolicy.NormalizeWithoutLinks(child);
            var childRelative = Path.GetRelativePath(root, child).Replace('\\', '/');
            ValidateRelativePath(childRelative);
            ReadEntry(root, child, childRelative, File.GetAttributes(child), result, ref total, cancellationToken, sources);
        }
    }

    private FileTreeEntry ReadFile(FileStream source, string relative, ref long total, CancellationToken cancellationToken,
        TreeReadLease.CaptureSources? sources)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var length = source.Length;
        total = checked(total + length);
        if (total > budget.MaximumTotalBytes) throw new IOException("snapshot_byte_limit");
        if (sources?.Blobs is { } blobs)
        {
            if (length <= BufferedCaptureBytes)
                return CaptureBufferedFile(source, relative, (int)length, blobs, sources.HashMeasurement, cancellationToken);
            var archived = blobs.PersistCapturedContent(source, length, cancellationToken);
            return new(relative, FileObjectKind.File, length, archived.Sha256);
        }

        string digest;
        using (var timing = sources is null ? default : StartupProfile.Measure(sources.HashMeasurement))
            digest = HashFile(source, length, cancellationToken);
        return new(relative, FileObjectKind.File, length, digest);
    }

    private static FileTreeEntry CaptureBufferedFile(FileStream source, string relative, int length, ImmutableBlobStore blobs,
        string hashMeasurement, CancellationToken cancellationToken)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
        try
        {
            string digest;
            using (StartupProfile.Measure(hashMeasurement))
            {
                var content = bytes.AsSpan(0, length);
                try { source.ReadExactly(content); }
                catch (EndOfStreamException) { throw new IOException("snapshot_source_changed"); }
                if (source.Length != length) throw new IOException("snapshot_source_changed");
                digest = Convert.ToHexStringLower(SHA256.HashData(content));
            }
            blobs.PersistCapturedBuffer(bytes, length, digest, cancellationToken);
            return new(relative, FileObjectKind.File, length, digest);
        }
        finally { ArrayPool<byte>.Shared.Return(bytes, clearArray: true); }
    }

    private static string HashFile(FileStream source, long length, CancellationToken cancellationToken)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Clamp(length, 1, 65536));
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (count == 0) throw new IOException("snapshot_source_changed");
                hasher.AppendData(buffer.AsSpan(0, count));
                remaining -= count;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        if (source.Length != length) throw new IOException("snapshot_source_changed");
        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    internal static void ValidateRelativePath(string relative)
    {
        PhysicalPathPolicy.ValidateRelativePath(relative);
    }

    private sealed class Utf8PathComparer : IComparer<byte[]>
    {
        internal static Utf8PathComparer Instance { get; } = new();
        public int Compare(byte[]? left, byte[]? right) => (left ?? []).AsSpan().SequenceCompareTo(right ?? []);
    }
}
