using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal sealed class InstallerDataStore(InstallerPaths paths, string profile)
{
    internal const string DirectoryName = "data-removal";
    private const string IntentName = "intent.protected";
    private const string OwnerName = "owner.protected";
    private const string PendingName = "owner.pending";
    internal string DirectoryPath => Path.Combine(paths.ManagerRoot, DirectoryName);
    internal bool Exists => File.Exists(DirectoryPath) || Directory.Exists(DirectoryPath);

    internal InstallerDataIntent Create(string root, Guid transactionId, InstallationIdentity identity,
        InstallerMetadataSnapshot metadata, FileTreeSnapshot runtime, PhysicalTreeObject[] objects, ProcessIdentity native)
    {
        InstallerCheckpointStore.RequireCaller(native);
        InstallerRuntimeGate.RequireHeld(root);
        if (transactionId == Guid.Empty || Exists) throw InstallationIdentityCodec.Invalid();
        using var manager = StableDirectoryLease.Acquire(paths.ManagerRoot);
        var installation = File.ReadAllBytes(Path.Combine(paths.ManagerRoot, InstallationAnchor.IdentityFile));
        if (InstallationIdentityCodec.Unseal(installation, root) != identity) throw InstallationIdentityCodec.Invalid();
        ExclusiveDirectory.Create(DirectoryPath);
        var intent = new InstallerDataIntent("nexus.installer.data-removal", 1, transactionId, root, profile,
            PhysicalObjectIdentity.Read(paths.ManagerRoot), PhysicalObjectIdentity.Read(DirectoryPath), installation,
            InstallerSnapshotCodec.Encode(metadata), runtime, objects, native, ProcessIdentityReader.Read(Environment.ProcessId), DateTimeOffset.UtcNow);
        byte[] bytes;
        try { bytes = InstallerDataCodec.Seal(intent); }
        catch
        {
            if (!Directory.EnumerateFileSystemEntries(DirectoryPath).Any())
                PhysicalObjectDeletion.DeleteVerified(new(DirectoryPath, intent.ManagerIdentity, intent.DirectoryIdentity, FileObjectKind.Directory, 0, null));
            throw;
        }
        WriteNew(IntentName, bytes);
        Claim(intent, transactionId, native);
        return intent;
    }

    internal InstallerDataIntent Read(string root)
    {
        var intent = InstallerDataCodec.Intent(ReadBytes(IntentName, InstallerDataCodec.MaximumBytes + 1024));
        if (!StringComparer.OrdinalIgnoreCase.Equals(intent.Root, PhysicalPathPolicy.NormalizeWithoutLinks(root)) || intent.Profile != profile ||
            PhysicalObjectIdentity.Read(paths.ManagerRoot) != intent.ManagerIdentity || PhysicalObjectIdentity.Read(DirectoryPath) != intent.DirectoryIdentity)
            throw InstallationIdentityCodec.Invalid();
        RequireMembers();
        _ = Claims(intent);
        return intent;
    }

    internal static InstallerDataIntent ReadPending(InstallerPaths paths)
    {
        var directory = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(paths.ManagerRoot, DirectoryName));
        var path = Path.Combine(directory, IntentName);
        PhysicalObjectIdentity.RequireSingleLink(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > InstallerDataCodec.MaximumBytes + 1024) throw InstallationIdentityCodec.Invalid();
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        var intent = InstallerDataCodec.Intent(bytes);
        if (intent.ManagerIdentity != PhysicalObjectIdentity.Read(paths.ManagerRoot) || intent.DirectoryIdentity != PhysicalObjectIdentity.Read(directory))
            throw InstallationIdentityCodec.Invalid();
        return intent;
    }

    internal void RequireStopped(InstallerDataIntent intent, ProcessIdentity native)
    {
        InstallerCheckpointStore.RequireCaller(native);
        var self = ProcessIdentityReader.Read(Environment.ProcessId);
        var identity = InstallationIdentityCodec.Unseal(intent.Installation, intent.Root);
        InstallerDataCodec.RequireActor(native, self, identity);
        foreach (var actor in Claims(intent).SelectMany(item => new[] { item.Data.Native, item.Data.Writer })
            .Concat(new[] { intent.Native, intent.Writer }).Distinct())
            if (actor != self && actor != native && WriterStopEvidence.ObserveProcess(actor) != WriterState.Stopped)
                throw new IOException("installer_data_writer_not_stopped");
    }

    internal void Claim(InstallerDataIntent intent, Guid transactionId, ProcessIdentity native)
    {
        InstallerRuntimeGate.RequireHeld(intent.Root);
        RequireStopped(intent, native);
        using var directory = StableDirectoryLease.Acquire(DirectoryPath);
        if (!InstallerDataCodec.Equal(Read(intent.Root), intent)) throw InstallationIdentityCodec.Invalid();
        var previous = Claims(intent);
        if (File.Exists(Path.Combine(DirectoryPath, PendingName)))
            File.Move(Path.Combine(DirectoryPath, PendingName), Path.Combine(DirectoryPath, OwnerName), overwrite: true);
        var current = previous.LastOrDefault();
        var self = ProcessIdentityReader.Read(Environment.ProcessId);
        if (current.Data is { } owned && owned.Native == native && owned.Writer == self && owned.TransactionId == transactionId) return;
        var parent = current.Sha256 ?? Digest(ReadBytes(IntentName, InstallerDataCodec.MaximumBytes + 1024));
        var intentDigest = Digest(ReadBytes(IntentName, InstallerDataCodec.MaximumBytes + 1024));
        var claim = new InstallerDataActor("nexus.installer.data-actor", 1, transactionId, intentDigest, parent, native, self);
        WriteNew(PendingName, InstallerDataCodec.Seal(claim));
        File.Move(Path.Combine(DirectoryPath, PendingName), Path.Combine(DirectoryPath, OwnerName), overwrite: true);
    }

    internal void RequireCurrent(InstallerDataIntent intent, Guid transactionId, ProcessIdentity native)
    {
        RequireStopped(intent, native);
        var claim = Claims(intent).LastOrDefault().Data ?? throw InstallationIdentityCodec.Invalid();
        if (claim.TransactionId != transactionId || claim.Native != native || claim.Writer != ProcessIdentityReader.Read(Environment.ProcessId))
            throw InstallationIdentityCodec.Invalid();
    }

    internal void Clear(InstallerDataIntent intent)
    {
        RequireMembers();
        var files = Directory.EnumerateFiles(DirectoryPath).Select(path => InstallerMetadataInventory.CaptureFile("data-removal", path)).ToArray();
        foreach (var file in files.OrderBy(file => file.Name == IntentName ? 1 : 0))
            PhysicalObjectDeletion.DeleteVerified(new(Path.Combine(DirectoryPath, file.Name), intent.DirectoryIdentity,
                file.Identity, FileObjectKind.File, file.SizeBytes, file.Sha256));
        PhysicalObjectDeletion.DeleteVerified(new(DirectoryPath, intent.ManagerIdentity, intent.DirectoryIdentity, FileObjectKind.Directory, 0, null));
    }

    private (InstallerDataActor Data, string Sha256)[] Claims(InstallerDataIntent intent)
    {
        var digest = Digest(ReadBytes(IntentName, InstallerDataCodec.MaximumBytes + 1024));
        var result = new List<(InstallerDataActor, string)>();
        foreach (var name in new[] { OwnerName, PendingName })
        {
            if (!File.Exists(Path.Combine(DirectoryPath, name))) continue;
            var bytes = ReadBytes(name, 65536);
            var claim = InstallerDataCodec.Actor(bytes);
            var identity = InstallationIdentityCodec.Unseal(intent.Installation, intent.Root);
            InstallerDataCodec.RequireActor(claim.Native, claim.Writer, identity);
            if (claim.IntentSha256 != digest || name == PendingName && claim.ParentSha256 != (result.LastOrDefault().Item2 ?? digest))
                throw InstallationIdentityCodec.Invalid();
            result.Add((claim, Digest(bytes)));
        }
        return result.ToArray();
    }

    private void RequireMembers()
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(DirectoryPath))
            if (Path.GetFileName(path) is not (IntentName or OwnerName or PendingName) ||
                (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw InstallationIdentityCodec.Invalid();
    }

    private byte[] ReadBytes(string name, int maximum)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(DirectoryPath, name));
        PhysicalObjectIdentity.RequireSingleLink(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length < 1 || input.Length > maximum) throw InstallationIdentityCodec.Invalid();
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
    }

    private void WriteNew(string name, byte[] bytes)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(DirectoryPath, name));
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
        output.Write(bytes); output.Flush(flushToDisk: true);
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
