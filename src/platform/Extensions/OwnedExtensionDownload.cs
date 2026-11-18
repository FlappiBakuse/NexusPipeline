using System.Runtime.Versioning;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Platform.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class OwnedExtensionDownload(RuntimeInstance instance, OwnedDownloadStaging staging, BlobReference reference) : IDisposable
{
    internal string ArchivePath => staging.ArchivePath;

    internal static async Task<OwnedExtensionDownload> Receive(RuntimeInstance instance, InstanceLease lease,
        ResourceLeaseManager resources, Guid operationId, ExtensionCatalogEntry entry,
        Func<Stream, CancellationToken, Task> transfer, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        if (operationId == Guid.Empty || entry.ArtifactBytes is < 1 or > StoreResponseReader.MaximumArtifactBytes ||
            entry.ArtifactSha256.Length != 64 || entry.ArtifactSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw Invalid();
        var download = await OwnedDownloadStaging.Receive(instance, lease, resources, "extension-" + operationId.ToString("N"),
            entry.ArtifactBytes, entry.ArtifactSha256, "extension_download_staging_unbound", "extension_download_cleanup_pending",
            transfer, cancellation).ConfigureAwait(false);
        return new(instance, download, new($"blobs/content/{entry.ArtifactSha256}.bin", entry.ArtifactBytes, entry.ArtifactSha256));
    }

    internal string Adopt(ImmutableBlobStore blobs, CancellationToken cancellation)
    {
        staging.RequireOwned(); cancellation.ThrowIfCancellationRequested();
        if (blobs.Instance != instance) throw Invalid();
        var destination = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, reference.RelativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var parent = StableDirectoryLease.Acquire(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination)) { blobs.Verify(reference); return destination; }
        if (!staging.TryTransferTo(destination, cancellation))
        { blobs.Verify(reference); return destination; }
        blobs.Verify(reference);
        staging.CompleteTransfer();
        return destination;
    }

    internal FileStream OpenRead() => staging.OpenRead();
    public void Dispose() => staging.Dispose();
    private static IOException Invalid() => new("extension_download_staging_unbound");
}
