using System.Runtime.Versioning;
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

    internal InstallerApplicationTarget Stage(string payloadRoot)
    {
        InstallerRuntimeGate.RequireHeld(frame.Root);
        var workspace = InstallerPayloadTree.Workspace(frame);
        using var held = StableDirectoryLease.Acquire(workspace);
        if (PhysicalObjectIdentity.Read(workspace) != frame.PayloadIdentity || frame.Stage != "payload") throw InstallationIdentityCodec.Invalid();
        var manifest = InstallerTargetManifest.Read(frame);
        using var source = InstallationPayload.Open(payloadRoot, frame.ManifestSha256, frame.Profile, exclusiveRoot: true);
        manifest.RequireInstalled(source.Snapshot);
        ExclusiveDirectory.Create(DirectoryPath);
        var identity = PhysicalObjectIdentity.Read(DirectoryPath);
        try
        {
            foreach (var entry in source.Snapshot.Entries.Where(entry => entry.Kind == FileObjectKind.File))
            {
                var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(DirectoryPath, entry.RelativePath));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = File.OpenRead(Path.Combine(source.Root, entry.RelativePath));
                using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                input.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }
            using var verified = source.Borrow();
            using var payload = InstallationPayload.Open(DirectoryPath, frame.ManifestSha256, frame.Profile, exclusiveRoot: true);
            manifest.RequireInstalled(payload.Snapshot);
            if (payload.RootIdentity != identity) throw InstallationIdentityCodec.Invalid();
            return new(payload.Snapshot, payload.Objects.ToArray());
        }
        catch
        {
            if (PhysicalObjectIdentity.Read(DirectoryPath) != identity) throw InstallationIdentityCodec.Invalid();
            var snapshot = new FileTreeScanner(SnapshotBudget.Default).Capture(DirectoryPath, default);
            InstallerApplicationObjects.DeleteRemaining(DirectoryPath, frame.PayloadIdentity!,
                InstallerApplicationObjects.Tree(snapshot, PhysicalTreeIdentity.Capture(DirectoryPath, snapshot)));
            throw;
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
