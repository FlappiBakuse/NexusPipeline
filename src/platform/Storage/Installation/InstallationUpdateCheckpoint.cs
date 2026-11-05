using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Maintenance;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Installation;

internal sealed record InstallationUpdateCommit(string Format, int Version, byte[] Checkpoint, byte[] Installation);
internal sealed record InstallationUpdateFrame(InstallerCheckpoint Checkpoint, InstallationIdentity Identity, byte[] Protected);

[SupportedOSPlatform("windows")]
internal sealed class InstallationUpdateCheckpoint(RuntimeInstance instance, ContractReader contracts)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, NumberHandling = JsonNumberHandling.Strict,
        RespectRequiredConstructorParameters = true
    };
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);

    internal BlobReference Prepare(InstallationUpdateSource source, HostUpdateSource update, FrozenPayloadApply plan, ProcessIdentity host)
    {
        var paths = InstallationUpdateBinding.Paths(source);
        var directory = Path.Combine(paths.ManagerRoot, "metadata-checkpoint");
        if (Directory.Exists(directory))
        {
            var restored = ReadPending(source, update, plan);
            if (restored.Checkpoint.Writer != host) throw Invalid();
            RequireFiles(restored, paths, original: true);
            return Persist(restored.Protected, ReadTarget(restored.Checkpoint, paths));
        }
        new InstallationUpdateBinding(instance, contracts).RequireCurrent(source, plan.TargetPath, checkpoint: false);
        ExclusiveDirectory.Create(directory);
        ExclusiveDirectory.Create(Path.Combine(directory, "before"));
        var frame = new InstallerCheckpoint("nexus.installer.checkpoint", 1, plan.OperationId, "prepared", plan.TargetPath,
            source.Profile, "installation", update.PayloadManifest.Sha256, InstallationUpdateBinding.Identity(source, plan.TargetPath).Worker.Sha256,
            PhysicalObjectIdentity.Read(paths.ManagerRoot), PhysicalObjectIdentity.Read(directory), PhysicalObjectIdentity.Read(Path.Combine(directory, "before")),
            host, host, DateTimeOffset.UtcNow, source.Metadata, null, null);
        var original = InstallationUpdateBinding.Identity(source, plan.TargetPath);
        var identity = original with { ManifestSha256 = update.PayloadManifest.Sha256, ProductVersion = update.HostVersion };
        var metadata = InstallerSnapshotCodec.Decode(source.Metadata, plan.TargetPath);
        metadata = metadata with { Uninstall = metadata.Uninstall with { Values = metadata.Uninstall.Values.Select(value =>
            value.Name == "DisplayVersion" ? value with { Text = update.HostVersion } : value).ToArray() } };
        var after = new InstallerIdentityPublication(paths).Stage(frame, identity, metadata);
        frame = frame with { Stage = "planned", After = InstallerSnapshotCodec.Encode(after), ParentSha256 = update.Installation!.Sha256 };
        var bytes = InstallerCheckpointCodec.Seal(frame);
        var result = Persist(bytes, ReadTarget(frame, paths));
        using var output = new FileStream(Path.Combine(directory, "planned.protected"), FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.WriteThrough);
        output.Write(bytes); output.Flush(flushToDisk: true);
        return result;
    }

    internal InstallationUpdateFrame Read(BlobReference reference, InstallationUpdateSource source, HostUpdateSource update, FrozenPayloadApply plan)
    {
        if (reference.SizeBytes is < 1 or > 2097152 || reference.RelativePath != "blobs/plans/" + reference.Sha256 + ".json") throw Invalid();
        using var held = _blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)held.Length)]; held.ReadExactly(bytes);
        using var document = StrictJson.Parse(bytes, 2097152, 1572864);
        var record = document.Deserialize<InstallationUpdateCommit>(Options) ?? throw Invalid();
        if (record.Format != "nexus.installation.update-commit" || record.Version != 1 || record.Checkpoint is null || record.Installation is null)
            throw Invalid();
        var frame = InstallerCheckpointCodec.Unseal(record.Checkpoint);
        var identity = InstallationIdentityCodec.Unseal(record.Installation, plan.TargetPath);
        RequireFrame(frame, identity, source, update, plan);
        var after = InstallerSnapshotCodec.Decode(frame.After!, frame.Root);
        var seal = after.Files.Single(file => file.Role == "manager" && file.Name == InstallationAnchor.IdentityFile);
        if (seal.SizeBytes != record.Installation.LongLength || seal.Sha256 != Convert.ToHexStringLower(SHA256.HashData(record.Installation))) throw Invalid();
        return new(frame, identity, record.Checkpoint);
    }

    internal InstallationUpdateFrame ReadPending(InstallationUpdateSource source, HostUpdateSource update, FrozenPayloadApply plan)
    {
        var paths = InstallationUpdateBinding.Paths(source);
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(paths.ManagerRoot, "metadata-checkpoint", "planned.protected"));
        PhysicalObjectIdentity.RequireSingleLink(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > 1048576) throw Invalid();
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        var frame = InstallerCheckpointCodec.Unseal(bytes);
        var target = ReadTarget(frame, paths);
        var identity = InstallationIdentityCodec.Unseal(target, plan.TargetPath);
        RequireFrame(frame, identity, source, update, plan);
        return new(frame, identity, bytes);
    }

    internal static void RequireFiles(InstallationUpdateFrame saved, InstallerPaths paths, bool original)
    {
        var frame = saved.Checkpoint;
        var directory = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(paths.ManagerRoot, "metadata-checkpoint"));
        if (PhysicalObjectIdentity.Read(directory) != frame.CheckpointIdentity ||
            PhysicalObjectIdentity.Read(paths.ManagerRoot) != frame.ManagerIdentity) throw Invalid();
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var name = Path.GetFileName(entry);
            var attributes = File.GetAttributes(entry);
            if (name is not ("before" or "identity.pending" or "planned.protected") ||
                (attributes & FileAttributes.ReparsePoint) != 0 ||
                (name == "before") != ((attributes & FileAttributes.Directory) != 0)) throw Invalid();
        }
        var backup = Path.Combine(directory, "before");
        if (Directory.Exists(backup))
        {
            if (PhysicalObjectIdentity.Read(backup) != frame.BackupIdentity) throw Invalid();
            var expected = InstallerSnapshotCodec.Decode(frame.Before, frame.Root).Files.Single(file =>
                file.Role == "manager" && file.Name == InstallationAnchor.IdentityFile);
            foreach (var entry in Directory.EnumerateFileSystemEntries(backup))
            {
                if (Path.GetFileName(entry) != "manager--" + InstallationAnchor.IdentityFile) throw Invalid();
                using var held = InstallerMetadataBackup.HoldFile(entry, expected, frame.BackupIdentity);
            }
        }
        var marker = Path.Combine(directory, "planned.protected");
        if (File.Exists(marker))
        {
            PhysicalPathPolicy.NormalizeWithoutLinks(marker);
            PhysicalObjectIdentity.RequireSingleLink(marker);
            using var held = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (held.Length != saved.Protected.LongLength) throw Invalid();
            var bytes = new byte[checked((int)held.Length)]; held.ReadExactly(bytes);
            if (!bytes.AsSpan().SequenceEqual(saved.Protected)) throw Invalid();
        }
        if (original)
        {
            if (PhysicalObjectIdentity.Read(backup) != frame.BackupIdentity || Directory.EnumerateFileSystemEntries(backup).Any()) throw Invalid();
            var target = InstallerSnapshotCodec.Decode(frame.After!, frame.Root).Files.Single(file =>
                file.Role == "manager" && file.Name == InstallationAnchor.IdentityFile);
            using var held = InstallerMetadataBackup.HoldFile(Path.Combine(directory, "identity.pending"), target, frame.CheckpointIdentity);
        }
    }

    private BlobReference Persist(byte[] bytes, byte[] target) =>
        _blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(new InstallationUpdateCommit("nexus.installation.update-commit", 1, bytes, target), Options));

    private static byte[] ReadTarget(InstallerCheckpoint frame, InstallerPaths paths)
    {
        var after = InstallerSnapshotCodec.Decode(frame.After ?? throw Invalid(), frame.Root);
        var proof = after.Files.Single(file => file.Role == "manager" && file.Name == InstallationAnchor.IdentityFile);
        var pending = Path.Combine(paths.ManagerRoot, "metadata-checkpoint", "identity.pending");
        var path = File.Exists(pending) ? pending : Path.Combine(paths.ManagerRoot, InstallationAnchor.IdentityFile);
        using var held = InstallerMetadataBackup.HoldFile(path, proof, File.Exists(pending) ? frame.CheckpointIdentity : frame.ManagerIdentity);
        if (held.Length > 65536) throw Invalid();
        var bytes = new byte[checked((int)held.Length)]; held.Position = 0; held.ReadExactly(bytes); return bytes;
    }

    private static void RequireFrame(InstallerCheckpoint frame, InstallationIdentity target, InstallationUpdateSource source,
        HostUpdateSource update, FrozenPayloadApply plan)
    {
        _ = InstallationUpdateBinding.Paths(source);
        var before = InstallerSnapshotCodec.Decode(source.Metadata, plan.TargetPath);
        var after = InstallerSnapshotCodec.Decode(frame.After ?? throw Invalid(), plan.TargetPath);
        var original = InstallationUpdateBinding.Identity(source, plan.TargetPath);
        if (frame.Stage != "planned" || frame.Scope != "installation" || frame.Profile != source.Profile ||
            frame.TargetManifest is not null || frame.OwnedSource is not null ||
            frame.TransactionId != plan.OperationId || frame.Root != plan.TargetPath || frame.ParentSha256 != update.Installation?.Sha256 ||
            frame.ManifestSha256 != update.PayloadManifest.Sha256 || frame.WorkerSha256 != original.Worker.Sha256 ||
            frame.ManagerIdentity != before.Directories.Single(item => item.Role == "manager").Identity ||
            frame.CheckpointIdentity.VolumeSerial != frame.ManagerIdentity.VolumeSerial || frame.BackupIdentity.VolumeSerial != frame.ManagerIdentity.VolumeSerial ||
            !frame.Before.AsSpan().SequenceEqual(source.Metadata) || frame.Writer != frame.SetupOwner ||
            frame.Writer.ImagePath != Path.Combine(plan.TargetPath, ApplicationTree.HostEntry) ||
            frame.Writer.ImageSha256 != plan.Candidate.Snapshot.Entries.Single(item => item.RelativePath == ApplicationTree.HostEntry).Sha256 ||
            target != (original with { ManifestSha256 = update.PayloadManifest.Sha256, ProductVersion = update.HostVersion })) throw Invalid();
        if (!InstallerMetadataInventory.Equal(before, after with { Files = before.Files, Ownership = before.Ownership, Uninstall = before.Uninstall }) ||
            !before.Files.Where(file => file.Role != "manager" || file.Name != InstallationAnchor.IdentityFile)
                .SequenceEqual(after.Files.Where(file => file.Role != "manager" || file.Name != InstallationAnchor.IdentityFile)) ||
            !InstallerMetadataInventory.Equal(before with { Uninstall = before.Uninstall with { Values = before.Uninstall.Values.Select(value =>
                value.Name == "DisplayVersion" ? value with { Text = update.HostVersion } : value).ToArray() } },
                before with { Uninstall = after.Uninstall })) throw Invalid();
        var seal = after.Files.Single(file => file.Role == "manager" && file.Name == InstallationAnchor.IdentityFile);
        var ownership = before.Ownership with { Values = before.Ownership.Values.Select(value =>
            value.Name == "IdentitySha256" ? value with { Text = seal.Sha256 } : value).ToArray() };
        if (!InstallerMetadataInventory.Equal(before with { Ownership = ownership }, before with { Ownership = after.Ownership })) throw Invalid();
    }

    private static IOException Invalid() => InstallationUpdateBinding.Invalid();
}
