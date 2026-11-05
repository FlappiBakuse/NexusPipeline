using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal sealed class InstallerMetadataRecovery(InstallerPaths paths, InstallerCheckpointStore store)
{
    internal void RestoreCurrentMetadata(string root, Guid transactionId)
        => Restore(store.RequireCurrentSetup(root, transactionId));

    internal void RestoreStoppedMetadata(string root, Guid transactionId)
        => Restore(store.RequireStopped(root, transactionId));

    private void Restore(InstallerCheckpoint frame)
    {
        var before = InstallerSnapshotCodec.Decode(frame.Before, frame.Root);
        var after = frame.After is null ? before : InstallerSnapshotCodec.Decode(frame.After, frame.Root);
        var backup = new InstallerMetadataBackup(paths, store);
        using var root = StableDirectoryLease.Acquire(frame.Root);
        using var preserved = StableDirectoryLease.Acquire(backup.DirectoryPath);
        if (PhysicalObjectIdentity.Read(backup.DirectoryPath) != frame.BackupIdentity) throw InstallationIdentityCodec.Invalid();
        if (PhysicalObjectIdentity.Read(frame.Root) != before.RootIdentity) throw InstallationIdentityCodec.Invalid();
        var current = store.Capture(frame.Root);
        RequireFiles(frame, before, after, current, backup);
        RequireDirectories(frame, before, after, current);
        InstallerRegistryEffects.RequireRestorable(paths.UninstallRegistryKey, before.Uninstall, after.Uninstall);
        InstallerRegistryEffects.RequireRestorable(paths.RegistryKey, before.Ownership, after.Ownership);
        RestoreFiles(frame, before, current, backup);
        InstallerRegistryEffects.Restore(paths.UninstallRegistryKey, before.Uninstall, after.Uninstall);
        InstallerRegistryEffects.Restore(paths.RegistryKey, before.Ownership, after.Ownership);
        RemoveDirectories(frame, before, after);
        var expected = before with { Directories = before.Directories.Select(directory => directory.Role == "manager" && directory.Identity is null
            ? directory with { Identity = frame.ManagerIdentity } : directory).ToArray() };
        if (!InstallerMetadataInventory.Equal(expected, store.Capture(frame.Root)))
            throw InstallationIdentityCodec.Invalid();
    }

    private static string Key(InstallerMetadataFile file) => file.Role + "/" + file.Name;

    private static void RequireFiles(InstallerCheckpoint frame, InstallerMetadataSnapshot before, InstallerMetadataSnapshot after,
        InstallerMetadataSnapshot current, InstallerMetadataBackup backup)
    {
        var originals = before.Files.ToDictionary(Key, StringComparer.Ordinal);
        var installed = after.Files.ToDictionary(Key, StringComparer.Ordinal);
        foreach (var file in current.Files)
            if (file != originals.GetValueOrDefault(Key(file)) && file != installed.GetValueOrDefault(Key(file)))
                throw InstallationIdentityCodec.Invalid();
        var knownBackups = before.Files.Select(backup.BackupPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFileSystemEntries(backup.DirectoryPath))
            if (!knownBackups.Contains(path) || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw InstallationIdentityCodec.Invalid();
        foreach (var file in before.Files)
        {
            var preserved = backup.BackupPath(file);
            var original = current.Files.SingleOrDefault(item => Key(item) == Key(file)) == file;
            if (File.Exists(preserved))
            {
                using var held = InstallerMetadataBackup.HoldFile(preserved, file, frame.BackupIdentity);
                if (original) throw InstallationIdentityCodec.Invalid();
            }
            else if (!original) throw InstallationIdentityCodec.Invalid();
        }
    }

    private void RequireDirectories(InstallerCheckpoint frame, InstallerMetadataSnapshot before,
        InstallerMetadataSnapshot after, InstallerMetadataSnapshot current)
    {
        foreach (var directory in current.Directories)
        {
            var original = before.Directories.Single(item => item.Role == directory.Role).Identity;
            var installed = after.Directories.Single(item => item.Role == directory.Role).Identity;
            if (directory.Role == "manager" && original is null) original = frame.ManagerIdentity;
            if (original is not null ? directory.Identity != original : directory.Identity is not null && directory.Identity != installed)
                throw InstallationIdentityCodec.Invalid();
            if (before.Directories.Single(item => item.Role == directory.Role).Identity is not null || directory.Identity is null) continue;
            var known = current.Files.Where(file => file.Role == directory.Role).Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var entry in Directory.EnumerateFileSystemEntries(InstallerMetadataInventory.DirectoryPath(frame.Root, paths, directory.Role)))
            {
                if (known.Contains(Path.GetFileName(entry))) continue;
                if (directory.Role == "manager" && Path.GetFileName(entry) == "metadata-checkpoint" &&
                    Directory.Exists(entry) && PhysicalObjectIdentity.Read(entry) == frame.CheckpointIdentity) continue;
                throw InstallationIdentityCodec.Invalid();
            }
        }
    }

    private static void RestoreFiles(InstallerCheckpoint frame, InstallerMetadataSnapshot before,
        InstallerMetadataSnapshot current, InstallerMetadataBackup backup)
    {
        var originals = before.Files.ToDictionary(Key, StringComparer.Ordinal);
        foreach (var file in current.Files)
        {
            if (file == originals.GetValueOrDefault(Key(file))) continue;
            var path = backup.SourcePath(frame.Root, file);
            PhysicalObjectDeletion.DeleteVerified(new(path, file.ParentIdentity, file.Identity, FileObjectKind.File, file.SizeBytes, file.Sha256));
        }
        foreach (var file in before.Files)
        {
            var source = backup.BackupPath(file);
            if (!File.Exists(source)) continue;
            using var held = InstallerMetadataBackup.HoldFile(source, file, frame.BackupIdentity);
            PhysicalObjectMove.MoveVerified(new(source, frame.BackupIdentity, file.Identity,
                backup.SourcePath(frame.Root, file), file.ParentIdentity, FileObjectKind.File));
        }
    }

    private void RemoveDirectories(InstallerCheckpoint frame, InstallerMetadataSnapshot before, InstallerMetadataSnapshot after)
    {
        foreach (var directory in after.Directories.Where(directory => directory.Role != "manager" && directory.Identity is not null))
        {
            if (before.Directories.Single(item => item.Role == directory.Role).Identity is not null) continue;
            var path = InstallerMetadataInventory.DirectoryPath(frame.Root, paths, directory.Role);
            if (!Directory.Exists(path)) continue;
            if (PhysicalObjectIdentity.Read(path) != directory.Identity || Directory.EnumerateFileSystemEntries(path).Any())
                throw InstallationIdentityCodec.Invalid();
            PhysicalObjectDeletion.DeleteVerified(new(path, PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!),
                directory.Identity!, FileObjectKind.Directory, 0, null));
        }
    }
}
