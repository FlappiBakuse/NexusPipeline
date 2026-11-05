using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal sealed class InstallerCheckpointCompletion(InstallerPaths paths, InstallerCheckpointStore store)
{
    internal void CompleteCurrent(string root, Guid transactionId, bool committed)
        => Complete(store.RequireCurrentSetup(root, transactionId), committed);

    internal void CompleteStopped(string root, Guid transactionId, bool committed = false)
        => Complete(store.RequireStopped(root, transactionId), committed);

    internal void CompleteFrozen(InstallerCheckpoint frame, bool committed)
    {
        if (frame.Stage != "planned" || frame.Scope != "installation") throw InstallationIdentityCodec.Invalid();
        // Maintenance records the target in its own durable startup commit before publishing it.
        Complete(committed ? frame with { Stage = "observed" } : frame, committed);
    }

    private void Complete(InstallerCheckpoint frame, bool committed)
    {
        var root = frame.Root;
        if (committed && frame.Stage != "observed") throw InstallationIdentityCodec.Invalid();
        var expected = InstallerSnapshotCodec.Decode(committed ? frame.After! : frame.Before, root);
        expected = expected with { Directories = expected.Directories.Select(directory => directory.Role == "manager" && directory.Identity is null
            ? directory with { Identity = frame.ManagerIdentity } : directory).ToArray() };
        if (!InstallerMetadataInventory.Equal(expected, InstallerMetadataInventory.CaptureFrozen(expected, paths, frame.Scope)))
            throw InstallationIdentityCodec.Invalid();
        var before = InstallerSnapshotCodec.Decode(frame.Before, root);
        var backup = new InstallerMetadataBackup(paths, store);
        var checkpoint = Path.GetDirectoryName(backup.DirectoryPath)!;
        if (PhysicalObjectIdentity.Read(checkpoint) != frame.CheckpointIdentity ||
            PhysicalObjectIdentity.Read(paths.ManagerRoot) != frame.ManagerIdentity) throw InstallationIdentityCodec.Invalid();
        if (!Directory.Exists(backup.DirectoryPath))
        {
            if (Directory.EnumerateFileSystemEntries(checkpoint).Any()) throw InstallationIdentityCodec.Invalid();
            PhysicalObjectDeletion.DeleteVerified(new(checkpoint, frame.ManagerIdentity, frame.CheckpointIdentity, FileObjectKind.Directory, 0, null));
            return;
        }
        if (PhysicalObjectIdentity.Read(backup.DirectoryPath) != frame.BackupIdentity) throw InstallationIdentityCodec.Invalid();
        var originals = before.Files.ToDictionary(backup.BackupPath, StringComparer.OrdinalIgnoreCase);
        var files = new List<(string Path, InstallerMetadataFile Proof)>();
        foreach (var path in Directory.EnumerateFileSystemEntries(backup.DirectoryPath))
        {
            if (!committed || !originals.TryGetValue(path, out var original)) throw InstallationIdentityCodec.Invalid();
            using var held = InstallerMetadataBackup.HoldFile(path, original, frame.BackupIdentity);
            files.Add((path, original with { ParentIdentity = frame.BackupIdentity }));
        }
        var directory = Path.GetDirectoryName(backup.DirectoryPath)!;
        foreach (var path in Directory.EnumerateFileSystemEntries(directory).Where(path => path != backup.DirectoryPath))
        {
            var name = Path.GetFileName(path);
            if (name is "worker.pending" or "identity.pending")
            {
                if (frame.After is null) throw InstallationIdentityCodec.Invalid();
                var target = InstallerSnapshotCodec.Decode(frame.After, root).Files.SingleOrDefault(file => file.Role == "manager" &&
                    file.Name == (name == "worker.pending" ? InstallationAnchor.WorkerFile : InstallationAnchor.IdentityFile))
                    ?? throw InstallationIdentityCodec.Invalid();
                using var held = InstallerMetadataBackup.HoldFile(path, target, frame.CheckpointIdentity);
                files.Add((path, target with { ParentIdentity = frame.CheckpointIdentity }));
            }
            else if (name is "prepared.protected" or "payload.protected" or "target.protected" or "planned.protected" or "observed.protected" or "committed.protected")
                files.Add((path, InstallerMetadataInventory.CaptureFile("checkpoint", path)));
            else throw InstallationIdentityCodec.Invalid();
        }
        foreach (var (path, proof) in files.OrderBy(item => Path.GetFileName(item.Path) switch
            { "committed.protected" => 2, "prepared.protected" => 1, _ => 0 }))
            PhysicalObjectDeletion.DeleteVerified(new(path, proof.ParentIdentity, proof.Identity, FileObjectKind.File, proof.SizeBytes, proof.Sha256));
        PhysicalObjectDeletion.DeleteVerified(new(backup.DirectoryPath, frame.CheckpointIdentity, frame.BackupIdentity, FileObjectKind.Directory, 0, null));
        PhysicalObjectDeletion.DeleteVerified(new(directory, frame.ManagerIdentity, frame.CheckpointIdentity, FileObjectKind.Directory, 0, null));
        if (!committed && before.Directories.Single(item => item.Role == "manager").Identity is null)
            PhysicalObjectDeletion.DeleteVerified(new(paths.ManagerRoot, PhysicalObjectIdentity.Read(Path.GetDirectoryName(paths.ManagerRoot)!),
                frame.ManagerIdentity, FileObjectKind.Directory, 0, null));
    }
}
