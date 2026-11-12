using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Archives;

namespace NexusPipeline.Platform.Extensions;

internal sealed record ExtensionPackageFile(string RelativePath, long SizeBytes, string Sha256, uint Crc32);

internal sealed class ExtensionPackageArchive : IDisposable
{
    internal const int MaximumFiles = 1024;
    internal const long MaximumPackageBytes = 268435456;
    internal const int MaximumCompressionRatio = 1000;
    private readonly PayloadArchive _archive;
    internal ImmutableArray<ExtensionPackageFile> Files { get; }
    internal ImmutableArray<string> Directories => _archive.Directories;

    private ExtensionPackageArchive(PayloadArchive archive)
    {
        _archive = archive;
        Files = archive.Files.Select(file => new ExtensionPackageFile(file.RelativePath, file.SizeBytes, file.Sha256, file.Crc32)).ToImmutableArray();
    }

    internal static ExtensionPackageArchive Open(Stream input, CancellationToken cancellation = default) =>
        new(PayloadArchive.Open(input, new(MaximumFiles, MaximumPackageBytes), Invalid, PayloadArchiveKind.Extension, cancellation));
    internal static ExtensionPackageArchive OpenFrozen(Stream input, FileTreeSnapshot snapshot, CancellationToken cancellation) =>
        new(PayloadArchive.Open(input, new(MaximumFiles, MaximumPackageBytes), Invalid, PayloadArchiveKind.Extension, cancellation, snapshot.Entries));
    internal byte[] ReadManifest(CancellationToken cancellation = default) => _archive.ReadManifest(cancellation);
    internal byte[]? ReadDocument(string name, CancellationToken cancellation = default) =>
        _archive.ReadDocument(name, 512 * 1024, cancellation);
    internal ArchivedFileTree CaptureCandidate(ImmutableBlobStore blobs, CancellationToken cancellation) => _archive.CaptureCandidate(null, cancellation);
    internal void CopyFile(string name, Stream destination, CancellationToken cancellation) => _archive.CopyFile(name, destination, cancellation);
    public void Dispose() => _archive.Dispose();
    private static IOException Invalid() => new("extension_package_content_invalid");
}
