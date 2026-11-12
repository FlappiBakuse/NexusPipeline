using System.Buffers;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Extensions;

namespace NexusPipeline.Platform.Storage.Archives;

internal sealed record PayloadArchiveFile(string RelativePath, long SizeBytes, string Sha256, uint Crc32);

internal sealed record PayloadArchiveBudget(int MaximumFiles, long MaximumBytes);
internal enum PayloadArchiveKind { Extension, Application }

internal sealed class PayloadArchive : IDisposable
{
    private readonly PayloadArchiveBudget _budget;
    private readonly Func<IOException> _rejection;
    private readonly ZipArchive _zip;
    private readonly ImmutableDictionary<string, ZipArchiveEntry> _entries;
    private readonly PayloadArchiveKind _kind;
    private string ManifestPath => _kind == PayloadArchiveKind.Application ? ApplicationTree.ManifestPath : "manifest.json";
    internal ImmutableArray<PayloadArchiveFile> Files { get; }
    internal ImmutableArray<string> Directories { get; }

    private PayloadArchive(ZipArchive zip, ImmutableDictionary<string, ZipArchiveEntry> entries,
        ImmutableArray<PayloadArchiveFile> files, ImmutableArray<string> directories, PayloadArchiveBudget budget,
        Func<IOException> rejection, PayloadArchiveKind kind)
    { _zip = zip; _entries = entries; Files = files; Directories = directories; _budget = budget; _rejection = rejection; _kind = kind; }

    internal static PayloadArchive Open(Stream actualArchive, PayloadArchiveBudget budget, Func<IOException> rejection,
        PayloadArchiveKind kind, CancellationToken cancellation = default, IReadOnlyList<FileTreeEntry>? expected = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(kind) || budget.MaximumFiles is < 1 or > 4096 || budget.MaximumBytes is < 1 or > 1073741824) throw rejection();
        var headers = ExtensionZipMetadata.Read(actualArchive, cancellation, budget.MaximumBytes);
        var zip = new ZipArchive(actualArchive, ZipArchiveMode.Read, leaveOpen: true);
        try
        {
            if (zip.Entries.Count != headers.Length) throw rejection();
            var paths = new PackagePathSet();
            var entries = ImmutableDictionary.CreateBuilder<string, ZipArchiveEntry>(StringComparer.Ordinal);
            var files = ImmutableArray.CreateBuilder<PayloadArchiveFile>(); long total = 0;
            for (var index = 0; index < zip.Entries.Count; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                var entry = zip.Entries[index]; var header = headers[index];
                if (entry.Length != header.Length || entry.CompressedLength != header.CompressedLength ||
                    entry.Crc32 != header.Crc32 || entry.FullName != header.FullName) throw rejection();
                var name = paths.Add(entry);
                total = checked(total + entry.Length);
                if (total > budget.MaximumBytes) throw rejection();
                var frozen = expected?.SingleOrDefault(item => item.RelativePath == name);
                if (expected is not null && (frozen is null || frozen.SizeBytes != entry.Length ||
                    frozen.Kind != (entry.FullName.EndsWith('/') ? FileObjectKind.Directory : FileObjectKind.File))) throw rejection();
                var checksum = expected is null ? ReadEntry(entry, null, budget.MaximumBytes, rejection, cancellation) : frozen!.Sha256;
                if (entry.FullName.EndsWith('/')) continue;
                if (files.Count >= budget.MaximumFiles) throw rejection();
                entries.Add(name, entry); files.Add(new(name, entry.Length, checksum!, entry.Crc32));
            }
            if (!entries.ContainsKey(kind == PayloadArchiveKind.Application ? ApplicationTree.ManifestPath : "manifest.json")) throw rejection();
            return new(zip, entries.ToImmutable(), files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToImmutableArray(),
                paths.Directories.OrderBy(path => path, StringComparer.Ordinal).ToImmutableArray(), budget, rejection, kind);
        }
        catch { zip.Dispose(); throw; }
    }

    internal byte[] ReadManifest(CancellationToken cancellation = default)
    {
        var entry = _entries[ManifestPath];
        if (entry.Length is < 1 or > StrictJson.MaximumBytes) throw _rejection();
        using var bytes = new MemoryStream(checked((int)entry.Length));
        var digest = ReadEntry(entry, bytes, _budget.MaximumBytes, _rejection, cancellation);
        if (digest != Files.Single(file => file.RelativePath == ManifestPath).Sha256) throw _rejection();
        return bytes.ToArray();
    }

    internal void CopyFile(string name, Stream destination, CancellationToken cancellation = default)
    {
        if (!_entries.TryGetValue(name, out var entry)) throw _rejection();
        var digest = ReadEntry(entry, destination, _budget.MaximumBytes, _rejection, cancellation);
        if (digest != Files.Single(file => file.RelativePath == name).Sha256) throw _rejection();
    }

    internal byte[]? ReadDocument(string name, int maximumBytes, CancellationToken cancellation = default)
    {
        if (!_entries.TryGetValue(name, out var entry)) return null;
        if (entry.Length > maximumBytes) throw _rejection();
        using var bytes = new MemoryStream(checked((int)entry.Length));
        var digest = ReadEntry(entry, bytes, maximumBytes, _rejection, cancellation);
        if (digest != Files.Single(file => file.RelativePath == name).Sha256) throw _rejection();
        return bytes.ToArray();
    }

    internal ArchivedFileTree CaptureCandidate(ImmutableBlobStore? blobs, CancellationToken cancellation)
    {
        var files = _kind == PayloadArchiveKind.Application ? Files.Where(file => !file.RelativePath.StartsWith("package-info/", StringComparison.Ordinal)).ToArray() : Files.ToArray();
        var directories = _kind == PayloadArchiveKind.Application
            ? Directories.Where(path => path.Length == 0 || files.Any(file => file.RelativePath.StartsWith(path + "/", StringComparison.Ordinal))).ToArray()
            : Directories.ToArray();
        var contents = new List<BlobReference>();
        foreach (var file in files)
        {
            cancellation.ThrowIfCancellationRequested();
            var reference = new BlobReference($"blobs/content/{file.Sha256}.bin", file.SizeBytes, file.Sha256);
            if (blobs is not null)
            {
                using var input = _entries[file.RelativePath].Open();
                blobs.PersistContent(input, reference, cancellation);
            }
            contents.Add(reference);
        }
        var tree = directories.Select(path => new FileTreeEntry(path, FileObjectKind.Directory, 0, null))
            .Concat(files.Select(file => new FileTreeEntry(file.RelativePath, FileObjectKind.File, file.SizeBytes, file.Sha256)))
            .OrderBy(entry => System.Text.Encoding.UTF8.GetBytes(entry.RelativePath),
                Comparer<byte[]>.Create((left, right) => left.AsSpan().SequenceCompareTo(right))).ToArray();
        var result = new ArchivedFileTree(new(FileObjectKind.Directory, Array.AsReadOnly(tree)), [],
            Array.AsReadOnly(contents.Distinct().ToArray()));
        FileTreeArchive.ValidateArchive(result); return result;
    }

    private static string ReadEntry(ZipArchiveEntry entry, Stream? destination, long maximumBytes, Func<IOException> rejection, CancellationToken cancellation)
    {
        using var input = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // .NET 10 exposes central-directory CRC metadata but does not validate it when entries are read.
        var crc = new ExtensionZipChecksum(); var buffer = ArrayPool<byte>.Shared.Rent(65536); long count = 0;
        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var read = input.Read(buffer); if (read == 0) break;
                count = checked(count + read);
                if (count > entry.Length || count > maximumBytes) throw rejection();
                hash.AppendData(buffer, 0, read); crc.Append(buffer.AsSpan(0, read));
                destination?.Write(buffer, 0, read);
            }
            if (count != entry.Length || crc.Value != entry.Crc32) throw rejection();
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    public void Dispose() => _zip.Dispose();
}
