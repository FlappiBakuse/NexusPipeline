using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal sealed class InstallerTargetTree(InstallerCheckpoint frame)
{
    internal string DirectoryPath => Path.Combine(InstallerPayloadTree.Workspace(frame), "target");
    private InstallerApplicationTarget Target => frame.TargetApplication ?? throw InstallationIdentityCodec.Invalid();
    private PhysicalObjectIdentity TargetIdentity => Target.Objects.Single(item => item.RelativePath.Length == 0).Identity;
    private ArchivedFileTree TargetTree => InstallerApplicationObjects.Tree(Target.Snapshot, Target.Objects);

    internal InstallerApplicationTarget Stage(string payloadRoot, Action? fileStaged = null)
    {
        InstallerRuntimeGate.RequireHeld(frame.Root);
        var workspace = InstallerPayloadTree.Workspace(frame);
        using var held = StableDirectoryLease.Acquire(workspace);
        if (PhysicalObjectIdentity.Read(workspace) != frame.PayloadIdentity || frame.Stage != "payload") throw InstallationIdentityCodec.Invalid();
        var manifest = InstallerTargetManifest.Read(frame);
        using var source = InstallationPayload.Open(payloadRoot, frame.ManifestSha256, frame.Profile, exclusiveRoot: true);
        manifest.RequireInstalled(source.Snapshot);
        var owned = new List<FileObjectDeletion>();
        var directories = new Dictionary<string, PhysicalObjectIdentity>(StringComparer.OrdinalIgnoreCase) { [workspace] = frame.PayloadIdentity! };
        try
        {
            var identity = CreateDirectory(DirectoryPath);
            foreach (var entry in source.Snapshot.Entries.Where(entry => entry.Kind == FileObjectKind.File))
            {
                var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(DirectoryPath, entry.RelativePath));
                var parent = Path.GetDirectoryName(path)!;
                var parentIdentity = CreateDirectory(parent);
                using var parentHandle = PhysicalDirectoryHandle.Open(parent, parentIdentity);
                using var input = File.OpenRead(Path.Combine(source.Root, entry.RelativePath));
                using var destination = new FileStream(parentHandle.CreateNewFile(Path.GetFileName(path), writeThrough: true), FileAccess.ReadWrite);
                var fileIdentity = PhysicalObjectIdentity.Read(destination.SafeFileHandle);
                try
                {
                    input.CopyTo(destination);
                    destination.Flush(flushToDisk: true);
                    owned.Add(new(path, parentIdentity, fileIdentity, FileObjectKind.File, entry.SizeBytes, entry.Sha256));
                }
                catch (Exception error)
                {
                    var cleanup = new CleanupFailures();
                    cleanup.Attempt(() =>
                    {
                        destination.Position = 0;
                        var digest = Convert.ToHexStringLower(SHA256.HashData(destination));
                        owned.Add(new(path, parentIdentity, fileIdentity, FileObjectKind.File, destination.Length, digest));
                    });
                    cleanup.Preserve(error);
                    throw;
                }
                fileStaged?.Invoke();
            }
            using var verified = source.Borrow();
            using var payload = InstallationPayload.Open(DirectoryPath, frame.ManifestSha256, frame.Profile, exclusiveRoot: true);
            manifest.RequireInstalled(payload.Snapshot);
            if (payload.RootIdentity != identity) throw InstallationIdentityCodec.Invalid();
            return new(payload.Snapshot, payload.Objects.ToArray());
        }
        catch (Exception error)
        {
            var cleanup = new CleanupFailures();
            for (var index = owned.Count - 1; index >= 0; index--)
            {
                var item = owned[index];
                cleanup.Attempt(() => PhysicalObjectDeletion.DeleteVerified(item));
            }
            cleanup.Preserve(error);
            throw;
        }

        PhysicalObjectIdentity CreateDirectory(string path)
        {
            if (directories.TryGetValue(path, out var existing)) return existing;
            var parent = Path.GetDirectoryName(path)!;
            var parentIdentity = CreateDirectory(parent);
            using var parentHandle = PhysicalDirectoryHandle.Open(parent, parentIdentity);
            using var directory = parentHandle.CreateNewDirectory(Path.GetFileName(path));
            var identity = directory.ReadIdentity();
            directories.Add(path, identity);
            owned.Add(new(path, parentIdentity, identity, FileObjectKind.Directory, 0, null));
            return identity;
        }
    }

    internal void Install()
    {
        InstallerRuntimeGate.RequireHeld(frame.Root);
        using var root = StableDirectoryLease.Acquire(frame.Root);
        using var target = StableDirectoryLease.Acquire(DirectoryPath);
        if (PhysicalObjectIdentity.Read(frame.Root) != frame.OwnedSource!.Identity.RootIdentity ||
            PhysicalObjectIdentity.Read(DirectoryPath) != TargetIdentity) throw InstallationIdentityCodec.Invalid();
        foreach (var name in ApplicationTree.Roots)
        {
            var tree = ApplicationTree.Slice(TargetTree, name);
            var source = Path.Combine(DirectoryPath, name);
            var installed = Path.Combine(frame.Root, name);
            if (File.Exists(source) || Directory.Exists(source))
            {
                InstallerApplicationObjects.Require(source, tree);
                if (File.Exists(installed) || Directory.Exists(installed)) throw InstallationIdentityCodec.Invalid();
                PhysicalObjectMove.MoveVerified(new(source, TargetIdentity, tree.OriginalIdentity!, installed,
                    frame.OwnedSource.Identity.RootIdentity, tree.Snapshot.Shape));
            }
            InstallerApplicationObjects.Require(installed, tree);
        }
    }

    internal void RequireInstalled()
    {
        foreach (var name in ApplicationTree.Roots)
            InstallerApplicationObjects.Require(Path.Combine(frame.Root, name), ApplicationTree.Slice(TargetTree, name));
    }

    internal void RemoveTarget()
    {
        InstallerRuntimeGate.RequireHeld(frame.Root);
        var originalTree = InstallerApplicationObjects.Tree(frame.OwnedSource!.Application, frame.OwnedSource.ApplicationObjects);
        foreach (var name in ApplicationTree.Roots)
        {
            var tree = ApplicationTree.Slice(TargetTree, name);
            var installed = Path.Combine(frame.Root, name);
            if (File.Exists(installed) || Directory.Exists(installed))
            {
                if (PhysicalObjectIdentity.Read(installed) == tree.OriginalIdentity) InstallerApplicationObjects.RequireRemaining(installed, tree);
                else InstallerApplicationObjects.Require(installed, ApplicationTree.Slice(originalTree, name));
            }
            InstallerApplicationObjects.RequireRemaining(Path.Combine(DirectoryPath, name), tree);
        }
        foreach (var name in ApplicationTree.Roots)
        {
            var tree = ApplicationTree.Slice(TargetTree, name);
            var installed = Path.Combine(frame.Root, name);
            var staged = Path.Combine(DirectoryPath, name);
            if (File.Exists(installed) || Directory.Exists(installed))
            {
                var identity = PhysicalObjectIdentity.Read(installed);
                if (identity == tree.OriginalIdentity)
                    InstallerApplicationObjects.DeleteRemaining(installed, frame.OwnedSource!.Identity.RootIdentity, tree);
                else
                {
                    var original = InstallerApplicationObjects.Tree(frame.OwnedSource!.Application, frame.OwnedSource.ApplicationObjects);
                    InstallerApplicationObjects.Require(installed, ApplicationTree.Slice(original, name));
                }
            }
            if (File.Exists(staged) || Directory.Exists(staged)) InstallerApplicationObjects.DeleteRemaining(staged, TargetIdentity, tree);
        }
        RemoveDirectory();
    }

    internal void Complete()
    {
        RequireInstalled();
        var original = InstallerApplicationObjects.Tree(frame.OwnedSource!.Application, frame.OwnedSource.ApplicationObjects);
        foreach (var name in ApplicationTree.Roots)
            InstallerApplicationObjects.RequireRemaining(Path.Combine(InstallerPayloadTree.Workspace(frame), name), ApplicationTree.Slice(original, name));
        foreach (var name in ApplicationTree.Roots)
            InstallerApplicationObjects.DeleteRemaining(Path.Combine(InstallerPayloadTree.Workspace(frame), name),
                frame.PayloadIdentity!, ApplicationTree.Slice(original, name));
        RemoveDirectory();
        var workspace = InstallerPayloadTree.Workspace(frame);
        if (Directory.Exists(workspace))
        {
            if (PhysicalObjectIdentity.Read(workspace) != frame.PayloadIdentity || Directory.EnumerateFileSystemEntries(workspace).Any())
                throw InstallationIdentityCodec.Invalid();
            PhysicalObjectDeletion.DeleteVerified(new(workspace, frame.OwnedSource.Identity.RootIdentity,
                frame.PayloadIdentity!, FileObjectKind.Directory, 0, null));
        }
    }

    private void RemoveDirectory()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            if (File.Exists(DirectoryPath)) throw InstallationIdentityCodec.Invalid();
            return;
        }
        if (PhysicalObjectIdentity.Read(DirectoryPath) != TargetIdentity || Directory.EnumerateFileSystemEntries(DirectoryPath).Any())
            throw InstallationIdentityCodec.Invalid();
        PhysicalObjectDeletion.DeleteVerified(new(DirectoryPath, frame.PayloadIdentity!, TargetIdentity, FileObjectKind.Directory, 0, null));
    }
}
