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
    internal Action<long>? HashBytes { get; init; }

    public FileTreeSnapshot Capture(string selectedRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (budget.MaximumEntries < 1 || budget.MaximumTotalBytes < 0)
            throw new ArgumentException("Invalid snapshot budget");
        var root = PhysicalPathPolicy.NormalizeWithoutLinks(selectedRoot);
        var first = Scan(root, cancellationToken);
        var second = Scan(root, cancellationToken);
        if (first.Shape != second.Shape || !first.Entries.SequenceEqual(second.Entries))
            throw new IOException("snapshot_source_changed");
        return first;
    }

    internal FileTreeSnapshot CaptureArchive(string selectedRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (budget.MaximumEntries < 1 || budget.MaximumTotalBytes < 0)
            throw new ArgumentException("Invalid snapshot budget");
        return Scan(PhysicalPathPolicy.NormalizeWithoutLinks(selectedRoot), cancellationToken);
    }

    private FileTreeSnapshot Scan(string root, CancellationToken cancellationToken)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(root); }
        catch (FileNotFoundException) { return new(FileObjectKind.Missing, []); }
        catch (DirectoryNotFoundException) { return new(FileObjectKind.Missing, []); }
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("snapshot_link_rejected");
        var result = new List<FileTreeEntry>();
        long total = 0;
        var shape = (attributes & FileAttributes.Directory) != 0 ? FileObjectKind.Directory : FileObjectKind.File;
        if (shape == FileObjectKind.File) result.Add(ReadFile(root, "", ref total, cancellationToken));
        else
        {
            result.Add(new("", FileObjectKind.Directory, 0, null));
            ReadDirectory(root, root, result, ref total, cancellationToken);
        }
        if (result.Select(entry => entry.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
            throw new IOException("snapshot_path_collision");
        return new(shape, Array.AsReadOnly(result.OrderBy(entry => Encoding.UTF8.GetBytes(entry.RelativePath), Utf8PathComparer.Instance).ToArray()));
    }

    private void ReadDirectory(string root, string directory, List<FileTreeEntry> result, ref long total, CancellationToken cancellationToken)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Count >= budget.MaximumEntries) throw new IOException("snapshot_entry_limit");
            PhysicalPathPolicy.NormalizeWithoutLinks(path);
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            ValidateRelativePath(relative);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                result.Add(new(relative, FileObjectKind.Directory, 0, null));
                ReadDirectory(root, path, result, ref total, cancellationToken);
            }
            else result.Add(ReadFile(path, relative, ref total, cancellationToken));
        }
    }

    private FileTreeEntry ReadFile(string path, string relative, ref long total, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = source.Length;
        total = checked(total + length);
        if (total > budget.MaximumTotalBytes) throw new IOException("snapshot_byte_limit");
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = source.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                hasher.AppendData(buffer.AsSpan(0, count));
                HashBytes?.Invoke(count);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        var hash = Convert.ToHexStringLower(hasher.GetHashAndReset());
        if (source.Length != length) throw new IOException("snapshot_source_changed");
        return new(relative, FileObjectKind.File, length, hash);
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
