using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal sealed class InstallerPayloadTree(InstallerCheckpoint frame)
{
    private sealed record Location(string Name, ArchivedFileTree Tree, bool Preserved);

    internal static string Workspace(InstallerCheckpoint checkpoint)
        => PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(checkpoint.Root, ".nexus-setup-" + checkpoint.TransactionId.ToString("N")));

    internal void PreserveOriginal()
    {
        using var root = HoldRoot();
        using var workspace = StableDirectoryLease.Acquire(Workspace(frame));
        var locations = Read();
        foreach (var item in locations.Where(item => !item.Preserved))
        {
            PhysicalObjectMove.MoveVerified(new(Path.Combine(frame.Root, item.Name), frame.OwnedSource!.Identity.RootIdentity,
                item.Tree.OriginalIdentity!, Path.Combine(Workspace(frame), item.Name), frame.PayloadIdentity!, item.Tree.Snapshot.Shape));
            RequireTree(Path.Combine(Workspace(frame), item.Name), item.Tree);
        }
        if (Read().Any(item => !item.Preserved)) throw InstallationIdentityCodec.Invalid();
    }

    internal void RestoreOriginal()
    {
        using var root = HoldRoot();
        if (!Directory.Exists(Workspace(frame)))
        {
            if (File.Exists(Workspace(frame))) throw InstallationIdentityCodec.Invalid();
            foreach (var item in Trees()) RequireTree(Path.Combine(frame.Root, item.Name), item.Tree);
            return;
        }
        using var workspace = StableDirectoryLease.Acquire(Workspace(frame));
        var locations = Read();
        foreach (var item in locations.Where(item => item.Preserved))
        {
            PhysicalObjectMove.MoveVerified(new(Path.Combine(Workspace(frame), item.Name), frame.PayloadIdentity!, item.Tree.OriginalIdentity!,
                Path.Combine(frame.Root, item.Name), frame.OwnedSource!.Identity.RootIdentity, item.Tree.Snapshot.Shape));
            RequireTree(Path.Combine(frame.Root, item.Name), item.Tree);
        }
        if (Read().Any(item => item.Preserved)) throw InstallationIdentityCodec.Invalid();
    }

    internal void CompleteRestoration()
    {
        using var root = HoldRoot();
        foreach (var item in Trees()) RequireTree(Path.Combine(frame.Root, item.Name), item.Tree);
        var workspace = Workspace(frame);
        if (!Directory.Exists(workspace))
        {
            if (File.Exists(workspace)) throw InstallationIdentityCodec.Invalid();
            return;
        }
        if (PhysicalObjectIdentity.Read(workspace) != frame.PayloadIdentity || Directory.EnumerateFileSystemEntries(workspace).Any())
            throw InstallationIdentityCodec.Invalid();
        PhysicalObjectDeletion.DeleteVerified(new(workspace, frame.OwnedSource!.Identity.RootIdentity, frame.PayloadIdentity!, FileObjectKind.Directory, 0, null));
    }

    private StableDirectoryLease HoldRoot()
    {
        InstallerSourceBinding.Require(frame);
        InstallerRuntimeGate.RequireHeld(frame.Root);
        if (frame.PayloadIdentity is null) throw InstallationIdentityCodec.Invalid();
        var held = StableDirectoryLease.Acquire(frame.Root);
        if (PhysicalObjectIdentity.Read(frame.Root) == frame.OwnedSource!.Identity.RootIdentity) return held;
        held.Dispose(); throw InstallationIdentityCodec.Invalid();
    }

    private Location[] Read()
    {
        var workspace = Workspace(frame);
        if (PhysicalObjectIdentity.Read(workspace) != frame.PayloadIdentity) throw InstallationIdentityCodec.Invalid();
        foreach (var entry in Directory.EnumerateFileSystemEntries(workspace))
            if (!ApplicationTree.Roots.Contains(Path.GetFileName(entry), StringComparer.Ordinal)) throw InstallationIdentityCodec.Invalid();
        return Trees().Select(item =>
        {
            var current = Path.Combine(frame.Root, item.Name); var before = Path.Combine(workspace, item.Name);
            var installed = Matches(current, item.Tree);
            var preserved = Matches(before, item.Tree);
            if (installed == preserved || installed && Exists(before) || preserved && Exists(current)) throw InstallationIdentityCodec.Invalid();
            return new Location(item.Name, item.Tree, preserved);
        }).ToArray();
    }

    private (string Name, ArchivedFileTree Tree)[] Trees()
    {
        var source = frame.OwnedSource!;
        var contents = source.Application.Entries.Where(entry => entry.Kind == FileObjectKind.File)
            .Select(entry => new BlobReference("blobs/content/" + entry.Sha256 + ".bin", entry.SizeBytes, entry.Sha256!)).Distinct().ToArray();
        var tree = new ArchivedFileTree(source.Application, source.ApplicationObjects, contents);
        return ApplicationTree.Roots.Select(name => (name, ApplicationTree.Slice(tree, name))).ToArray();
    }

    private static bool Matches(string path, ArchivedFileTree tree)
    {
        var actual = new FileTreeScanner(SnapshotBudget.Default).Capture(path, default);
        if (actual.Shape != tree.Snapshot.Shape || !actual.Entries.SequenceEqual(tree.Snapshot.Entries)) return false;
        PhysicalTreeIdentity.RequireSingleLinks(path, actual);
        return PhysicalTreeIdentity.Capture(path, actual).SequenceEqual(tree.OriginalObjects);
    }

    private static void RequireTree(string path, ArchivedFileTree tree)
    {
        if (!Matches(path, tree)) throw InstallationIdentityCodec.Invalid();
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
