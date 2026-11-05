using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal sealed class InstallerIdentityPublication(InstallerPaths paths)
{
    internal InstallerMetadataSnapshot Stage(InstallerCheckpoint frame, InstallationIdentity identity, InstallerMetadataSnapshot metadata)
    {
        var bytes = InstallationIdentityCodec.Seal(identity);
        var path = Path.Combine(paths.ManagerRoot, "metadata-checkpoint", "identity.pending");
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { output.Write(bytes); output.Flush(flushToDisk: true); }
        var proof = InstallerMetadataInventory.CaptureFile("manager", path) with
            { Name = InstallationAnchor.IdentityFile, ParentIdentity = frame.ManagerIdentity };
        var files = metadata.Files.Where(file => file.Role != "manager" || file.Name != InstallationAnchor.IdentityFile).Append(proof)
            .OrderBy(file => file.Role, StringComparer.Ordinal).ThenBy(file => file.Name, StringComparer.Ordinal).ToArray();
        return metadata with { Files = files, Ownership = new(true, new[]
        {
            Text("ApplicationRoot", identity.ApplicationRoot), Text("IdentitySha256", Convert.ToHexStringLower(SHA256.HashData(bytes))),
            Text("InstallationId", identity.InstallationId.ToString("D"))
        }) };
    }

    internal InstallationIdentity Read(InstallerCheckpoint frame, bool original)
    {
        var snapshot = InstallerSnapshotCodec.Decode(original ? frame.Before : frame.After ?? throw InstallationIdentityCodec.Invalid(), frame.Root);
        var proof = snapshot.Files.SingleOrDefault(file => file.Role == "manager" && file.Name == InstallationAnchor.IdentityFile)
            ?? throw InstallationIdentityCodec.Invalid();
        var checkpoint = Path.Combine(paths.ManagerRoot, "metadata-checkpoint");
        var preserved = Path.Combine(checkpoint, "before", "manager--" + InstallationAnchor.IdentityFile);
        var pending = Path.Combine(checkpoint, "identity.pending");
        var source = original ? File.Exists(preserved) ? preserved : Path.Combine(paths.ManagerRoot, proof.Name)
            : File.Exists(pending) ? pending : Path.Combine(paths.ManagerRoot, proof.Name);
        var parent = source == preserved ? frame.BackupIdentity : source == pending ? frame.CheckpointIdentity : frame.ManagerIdentity;
        using var held = InstallerMetadataBackup.HoldFile(source, proof, parent);
        if (held.Length > 65536) throw InstallationIdentityCodec.Invalid();
        var bytes = new byte[checked((int)held.Length)]; held.Position = 0; held.ReadExactly(bytes);
        var identity = InstallationIdentityCodec.Unseal(bytes, frame.Root);
        if (identity.RootIdentity != InstallerSnapshotCodec.Decode(frame.Before, frame.Root).RootIdentity ||
            !original && identity.ManifestSha256 != frame.ManifestSha256 || snapshot.Ownership.Values.Length != 3 ||
            snapshot.Ownership.Values.SingleOrDefault(value => value.Name == "IdentitySha256") is not { Kind: RegistryValueKind.String, Text: { } digest } ||
            digest != proof.Sha256 || snapshot.Ownership.Values.SingleOrDefault(value => value.Name == "InstallationId") is not
                { Kind: RegistryValueKind.String, Text: { } installation } || installation != identity.InstallationId.ToString("D") ||
            snapshot.Ownership.Values.SingleOrDefault(value => value.Name == "ApplicationRoot") is not
                { Kind: RegistryValueKind.String, Text: { } root } || root != identity.ApplicationRoot)
            throw InstallationIdentityCodec.Invalid();
        return identity;
    }

    internal void Publish(InstallerCheckpoint frame)
    {
        var before = InstallerSnapshotCodec.Decode(frame.Before, frame.Root);
        var target = InstallerSnapshotCodec.Decode(frame.After ?? throw InstallationIdentityCodec.Invalid(), frame.Root);
        var current = InstallerMetadataInventory.CaptureFrozen(target, paths, frame.Scope);
        if (!InstallerMetadataInventory.Equal(current with { Files = target.Files, Ownership = target.Ownership, Uninstall = target.Uninstall }, target) ||
            !current.Files.Where(file => file.Role != "manager").SequenceEqual(target.Files.Where(file => file.Role != "manager")))
            throw InstallationIdentityCodec.Invalid();
        var originals = before.Files.Where(file => file.Role == "manager").ToDictionary(file => file.Name, StringComparer.Ordinal);
        var installed = target.Files.Where(file => file.Role == "manager").ToDictionary(file => file.Name, StringComparer.Ordinal);
        if (current.Files.Where(file => file.Role == "manager").Any(file =>
            file != originals.GetValueOrDefault(file.Name) && file != installed.GetValueOrDefault(file.Name))) throw InstallationIdentityCodec.Invalid();
        InstallerRegistryEffects.RequireRestorable(paths.RegistryKey, before.Ownership, target.Ownership);
        InstallerRegistryEffects.RequireRestorable(paths.UninstallRegistryKey, target.Uninstall, before.Uninstall);
        var backup = new InstallerMetadataBackup(paths, new(paths, frame.Profile, frame.Scope));
        var heldFiles = new List<FileStream>();
        try
        {
            foreach (var file in originals.Values.Where(file => file != installed.GetValueOrDefault(file.Name)))
            {
                var source = Path.Combine(paths.ManagerRoot, file.Name);
                var preserved = backup.BackupPath(file);
                var original = current.Files.Contains(file);
                if (original && File.Exists(preserved)) throw InstallationIdentityCodec.Invalid();
                heldFiles.Add(InstallerMetadataBackup.HoldFile(original ? source : preserved, file,
                    original ? frame.ManagerIdentity : frame.BackupIdentity));
            }
            foreach (var file in installed.Values)
            {
                var pending = Pending(file.Name);
                var published = current.Files.Contains(file);
                if (published && File.Exists(pending)) throw InstallationIdentityCodec.Invalid();
                heldFiles.Add(InstallerMetadataBackup.HoldFile(published ? Path.Combine(paths.ManagerRoot, file.Name) : pending,
                    file, published ? frame.ManagerIdentity : frame.CheckpointIdentity));
            }
            InstallerRegistryEffects.Restore(paths.UninstallRegistryKey, target.Uninstall, before.Uninstall);
            foreach (var file in originals.Values.Where(file => file != installed.GetValueOrDefault(file.Name) && current.Files.Contains(file)))
                PhysicalObjectMove.MoveVerified(new(Path.Combine(paths.ManagerRoot, file.Name), frame.ManagerIdentity, file.Identity,
                    backup.BackupPath(file), frame.BackupIdentity, FileObjectKind.File));
            foreach (var file in installed.Values.Where(file => !current.Files.Contains(file)))
                PhysicalObjectMove.MoveVerified(new(Pending(file.Name), frame.CheckpointIdentity, file.Identity,
                    Path.Combine(paths.ManagerRoot, file.Name), frame.ManagerIdentity, FileObjectKind.File));
            InstallerRegistryEffects.Restore(paths.RegistryKey, target.Ownership, before.Ownership);
        }
        finally { foreach (var held in heldFiles) held.Dispose(); }
    }

    internal void DiscardUnplanned(InstallerCheckpoint frame)
    {
        if (frame.Stage != "target" || frame.OwnedSource is null) throw InstallationIdentityCodec.Invalid();
        var path = Pending(InstallationAnchor.IdentityFile);
        if (!File.Exists(path)) return;
        var proof = InstallerMetadataInventory.CaptureFile("manager", path);
        using (var held = InstallerMetadataBackup.HoldFile(path, proof, frame.CheckpointIdentity))
        {
            if (held.Length > 65536) throw InstallationIdentityCodec.Invalid();
            var bytes = new byte[checked((int)held.Length)]; held.Position = 0; held.ReadExactly(bytes);
            var expected = frame.OwnedSource.Identity with { ManifestSha256 = frame.ManifestSha256,
                ProductVersion = InstallerTargetManifest.Read(frame).ProductVersion };
            if (InstallationIdentityCodec.Unseal(bytes, frame.Root) != expected) throw InstallationIdentityCodec.Invalid();
        }
        PhysicalObjectDeletion.DeleteVerified(new(path, frame.CheckpointIdentity, proof.Identity, FileObjectKind.File, proof.SizeBytes, proof.Sha256));
    }

    private string Pending(string name) => Path.Combine(paths.ManagerRoot, "metadata-checkpoint", name switch
    {
        InstallationAnchor.IdentityFile => "identity.pending", InstallationAnchor.WorkerFile => "worker.pending",
        _ => throw InstallationIdentityCodec.Invalid()
    });

    private static InstallerRegistryValue Text(string name, string text) => new(name, RegistryValueKind.String, text, null, null, null);
}
