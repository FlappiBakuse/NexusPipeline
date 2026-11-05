using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal sealed class InstallerMetadataBackup(InstallerPaths paths, InstallerCheckpointStore store)
{
    internal string DirectoryPath => PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(paths.ManagerRoot, "metadata-checkpoint", "before"));
    internal void Preposition(string root, Guid transactionId)
    {
        var frame = store.Read(root, transactionId);
        InstallerCheckpointStore.RequireCaller(frame.SetupOwner);
        if (frame.Stage != "prepared") throw InstallationIdentityCodec.Invalid();
        var snapshot = InstallerSnapshotCodec.Decode(frame.Before, root);
        using var backup = HoldDirectory(frame);
        RequireMembers(frame, snapshot);
        foreach (var file in snapshot.Files)
        {
            var source = SourcePath(root, file);
            var destination = BackupPath(file);
            if (File.Exists(destination))
            {
                using var held = HoldFile(destination, file, frame.BackupIdentity);
                if (File.Exists(source) || Directory.Exists(source)) throw InstallationIdentityCodec.Invalid();
                continue;
            }
            using var original = HoldFile(source, file, file.ParentIdentity);
            PhysicalObjectMove.MoveVerified(new(source, file.ParentIdentity, file.Identity,
                destination, frame.BackupIdentity, FileObjectKind.File));
        }
    }

    internal void RequirePreserved(string root, Guid transactionId)
    {
        var frame = store.Read(root, transactionId);
        var snapshot = InstallerSnapshotCodec.Decode(frame.Before, root);
        using var directory = HoldDirectory(frame);
        RequireMembers(frame, snapshot);
        foreach (var file in snapshot.Files)
        {
            using var held = HoldFile(BackupPath(file), file, frame.BackupIdentity);
        }
    }

    internal string SourcePath(string root, InstallerMetadataFile file)
    {
        if (!InstallerMetadataInventory.KnownFile(file.Role, file.Name)) throw InstallationIdentityCodec.Invalid();
        return Path.Combine(InstallerMetadataInventory.DirectoryPath(root, paths, file.Role), file.Name);
    }

    internal string BackupPath(InstallerMetadataFile file)
    {
        if (!InstallerMetadataInventory.KnownFile(file.Role, file.Name)) throw InstallationIdentityCodec.Invalid();
        return PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(DirectoryPath, file.Role + "--" + file.Name));
    }

    private StableDirectoryLease HoldDirectory(InstallerCheckpoint frame)
    {
        var root = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(paths.ManagerRoot, "metadata-checkpoint", "before"));
        var held = StableDirectoryLease.Acquire(root);
        if (PhysicalObjectIdentity.Read(root) == frame.BackupIdentity) return held;
        held.Dispose(); throw InstallationIdentityCodec.Invalid();
    }

    private void RequireMembers(InstallerCheckpoint frame, InstallerMetadataSnapshot snapshot)
    {
        var directory = Path.Combine(paths.ManagerRoot, "metadata-checkpoint", "before");
        var expected = snapshot.Files.Select(file => file.Role + "--" + file.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            if (!expected.Contains(Path.GetFileName(entry)) || (File.GetAttributes(entry) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw InstallationIdentityCodec.Invalid();
        if (PhysicalObjectIdentity.Read(directory) != frame.BackupIdentity) throw InstallationIdentityCodec.Invalid();
    }

    internal static FileStream HoldFile(string requestedPath, InstallerMetadataFile expected, PhysicalObjectIdentity parentIdentity)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(requestedPath);
        PhysicalObjectIdentity.RequireSingleLink(path);
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try
        {
            if (PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!) != parentIdentity ||
                PhysicalObjectIdentity.Read(held.SafeFileHandle) != expected.Identity || held.Length != expected.SizeBytes ||
                Convert.ToHexStringLower(SHA256.HashData(held)) != expected.Sha256) throw InstallationIdentityCodec.Invalid();
            return held;
        }
        catch { held.Dispose(); throw; }
    }
}
