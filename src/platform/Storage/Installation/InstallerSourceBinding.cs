using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal static class InstallerSourceBinding
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    internal static void Require(InstallerOwnedSource source, InstallerMetadataSnapshot before, string profile, ProcessIdentity setup)
    {
        if (source.Format != "nexus.installer.owned-source" || source.Version != 1 || source.Profile != profile ||
            source.SetupOwner != setup || source.Identity is null || source.Application is null || source.ApplicationObjects is null ||
            source.Metadata is null || source.Application.Entries is null ||
            source.Application.Entries.Any(entry => entry is null || entry.RelativePath is null) ||
            source.ApplicationObjects.Any(item => item is null || item.RelativePath is null)) throw InstallationIdentityCodec.Invalid();
        InstallerSnapshotCodec.Validate(source.Metadata);
        if (!InstallerMetadataInventory.Equal(source.Metadata, before)) throw InstallationIdentityCodec.Invalid();
        var identity = source.Identity;
        InstallationIdentityCodec.Validate(identity);
        if (identity.State != "active" || identity.ApplicationRoot != before.Root || identity.RootIdentity != before.RootIdentity ||
            !before.Ownership.Exists) throw InstallationIdentityCodec.Invalid();
        InstallerRegistration.RequireNative(before, identity.ProductVersion);
        ApplicationTree.Validate(source.Application, complete: true);
        var files = source.Application.Entries.Where(entry => entry.Kind == FileObjectKind.File).ToArray();
        if (files.Any(file => !InstallationIdentityCodec.Digest(file.Sha256)) ||
            files.SingleOrDefault(file => file.RelativePath == ApplicationTree.ManifestPath)?.Sha256 != identity.ManifestSha256)
            throw InstallationIdentityCodec.Invalid();
        var contents = files.Select(file => new BlobReference("blobs/content/" + file.Sha256 + ".bin", file.SizeBytes, file.Sha256!))
            .Distinct().ToArray();
        FileTreeArchive.ValidateArchive(new(source.Application, source.ApplicationObjects, contents));
        if (!source.ApplicationObjects.Select(item => item.RelativePath).SequenceEqual(source.Application.Entries.Select(entry => entry.RelativePath)) ||
            source.ApplicationObjects.Any(item => !InstallationIdentityCodec.Physical(item.Identity)) ||
            source.ApplicationObjects.Single(item => item.RelativePath.Length == 0).Identity != identity.RootIdentity)
            throw InstallationIdentityCodec.Invalid();
        var worker = before.Files.SingleOrDefault(file => file.Role == "manager" && file.Name == InstallationAnchor.WorkerFile);
        var engine = before.Files.SingleOrDefault(file => file.Role == "uninstall" && file.Name == "unins000.exe");
        if (worker is null || engine is null || new InstallerFile(worker.SizeBytes, worker.Sha256, worker.Identity) != identity.Worker ||
            new InstallerFile(engine.SizeBytes, engine.Sha256, engine.Identity) != identity.NativeEngine)
            throw InstallationIdentityCodec.Invalid();
    }

    internal static void Require(InstallerCheckpoint frame)
    {
        if (frame.OwnedSource is not { } source || frame.Scope != "installation" || frame.TargetManifest is null)
            throw InstallationIdentityCodec.Invalid();
        Require(source, InstallerSnapshotCodec.Decode(frame.Before, frame.Root), frame.Profile, frame.SetupOwner);
    }

    internal static bool Equal(InstallerOwnedSource? left, InstallerOwnedSource? right)
        => left is null ? right is null : right is not null &&
            JsonSerializer.SerializeToUtf8Bytes(left, Options).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(right, Options));
}
