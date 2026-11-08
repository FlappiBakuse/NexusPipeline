using System.Collections.Immutable;
using System.Security.Cryptography;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Archives;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed record UpdatePayloadFile(string RelativePath, long SizeBytes, string Sha256, string Kind);
internal sealed record UpdatePayloadManifest(ApplicationManifest Application, UpdateStoreBinding StoreBinding, string Profile)
{
    internal string HostVersion => Application.HostVersion;
    internal string SdkVersion => SdkIdentity.Version;
    internal string HostEntry => ApplicationTree.HostEntry;
    internal string LauncherEntry => ApplicationTree.LauncherEntry;
    internal string MaintenanceEntry => ApplicationTree.MaintenanceEntry;
    internal string DesktopEntry => ApplicationTree.DesktopEntry;
    internal ImmutableArray<UpdatePayloadFile> Files => Application.Files;
}

internal sealed class UpdateManifestCodec(ContractReader contracts)
{
    internal UpdatePayloadManifest Read(ReadOnlyMemory<byte> original, HostUpdateRelease release,
        IReadOnlyList<PayloadArchiveFile>? archiveFiles = null, string? requiredProfile = "production")
    {
        if (original.Length is < 1 or > StrictJson.MaximumBytes ||
            original.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ||
            Convert.ToHexStringLower(SHA256.HashData(original.Span)) != release.PayloadManifestSha256) throw Invalid();
        var application = ApplicationManifest.Read(contracts, original, requiredProfile);
        if (application.HostVersion != release.HostVersion) throw Invalid();
        var result = new UpdatePayloadManifest(application, release.StoreBinding, requiredProfile ?? "production");
        if (archiveFiles is not null)
        {
            var actual = archiveFiles.Where(file => file.RelativePath != ApplicationTree.ManifestPath &&
                !file.RelativePath.StartsWith("package-info/", StringComparison.Ordinal));
            if (actual.Any(file => !result.Files.Any(expected => expected.RelativePath == file.RelativePath &&
                expected.SizeBytes == file.SizeBytes && expected.Sha256 == file.Sha256))) throw Invalid();
        }
        return result;
    }

    internal ApplicationManifest RequireOriginal(ArchivedFileTree before, ImmutableBlobStore blobs, string profile)
    {
        var file = before.Snapshot.Entries.SingleOrDefault(entry => entry.RelativePath == ApplicationTree.ManifestPath)
            ?? throw Invalid();
        var reference = before.Contents.Single(blob => blob.Sha256 == file.Sha256 && blob.SizeBytes == file.SizeBytes);
        if (reference.SizeBytes is < 1 or > StrictJson.MaximumBytes) throw Invalid();
        using var input = blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)reference.SizeBytes)]; input.ReadExactly(bytes);
        var manifest = ApplicationManifest.Read(contracts, bytes, profile);
        ApplicationManifest.RequireTree(before, manifest, reference, allowMissing: true);
        return manifest;
    }

    private static IOException Invalid() => new("update_payload_manifest_unbound");
}
