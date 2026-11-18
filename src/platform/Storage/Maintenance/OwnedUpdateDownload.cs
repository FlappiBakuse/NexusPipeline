using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;

namespace NexusPipeline.Platform.Storage.Maintenance;

[SupportedOSPlatform("windows")]
internal sealed class OwnedUpdateDownload(OwnedDownloadStaging staging) : IDisposable
{
    internal string ArchivePath => staging.ArchivePath;

    internal static async Task<OwnedUpdateDownload> Receive(RuntimeInstance instance, InstanceLease lease,
        ResourceLeaseManager resources, Guid operationId, HostUpdateArtifact artifact,
        Func<Stream, CancellationToken, Task> transfer, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        if (operationId == Guid.Empty || artifact.SizeBytes is < 1 or > 1073741824 ||
            artifact.Sha256.Length != 64 || artifact.Sha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new IOException("maintenance_download_staging_unbound");
        return new(await OwnedDownloadStaging.Receive(instance, lease, resources, "host-update-" + operationId.ToString("N"),
            artifact.SizeBytes, artifact.Sha256, "maintenance_download_staging_unbound", "maintenance_download_cleanup_pending",
            transfer, cancellation).ConfigureAwait(false));
    }

    public void Dispose() => staging.Dispose();
}
