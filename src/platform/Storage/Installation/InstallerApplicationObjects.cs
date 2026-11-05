using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Installation;

internal sealed record InstallerApplicationTarget(FileTreeSnapshot Snapshot, PhysicalTreeObject[] Objects);

[SupportedOSPlatform("windows")]
internal static class InstallerApplicationObjects
{
    internal static ArchivedFileTree Tree(FileTreeSnapshot snapshot, IReadOnlyList<PhysicalTreeObject> objects)
        => new(snapshot, objects, snapshot.Entries.Where(entry => entry.Kind == FileObjectKind.File)
            .Select(entry => new BlobReference("blobs/content/" + entry.Sha256 + ".bin", entry.SizeBytes, entry.Sha256!)).Distinct().ToArray());

    internal static void Require(string path, ArchivedFileTree expected)
    {
        var actual = new FileTreeScanner(SnapshotBudget.Default).Capture(path, default);
        if (actual.Shape != expected.Snapshot.Shape || !actual.Entries.SequenceEqual(expected.Snapshot.Entries))
            throw InstallationIdentityCodec.Invalid();
        PhysicalTreeIdentity.RequireSingleLinks(path, actual);
        if (!PhysicalTreeIdentity.Capture(path, actual).SequenceEqual(expected.OriginalObjects)) throw InstallationIdentityCodec.Invalid();
    }

    internal static void DeleteRemaining(string path, PhysicalObjectIdentity parent, ArchivedFileTree expected)
    {
        RequireRemaining(path, expected);
        var actual = new FileTreeScanner(SnapshotBudget.Default).Capture(path, default);
        if (actual.Shape == FileObjectKind.Missing) return;
        PhysicalTreeIdentity.RequireSingleLinks(path, actual);
        var objects = PhysicalTreeIdentity.Capture(path, actual);
        if (actual.Entries.Any(entry => !expected.Snapshot.Entries.Contains(entry)) ||
            objects.Any(item => !expected.OriginalObjects.Contains(item))) throw InstallationIdentityCodec.Invalid();
        foreach (var entry in actual.Entries.OrderByDescending(entry => entry.RelativePath.Count(character => character == '/'))
            .ThenByDescending(entry => entry.RelativePath.Length))
        {
            var target = entry.RelativePath.Length == 0 ? path : Path.Combine(path, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var identity = objects.Single(item => item.RelativePath == entry.RelativePath).Identity;
            var separator = entry.RelativePath.LastIndexOf('/');
            var parentPath = separator < 0 ? "" : entry.RelativePath[..separator];
            var parentIdentity = entry.RelativePath.Length == 0 ? parent : objects.Single(item => item.RelativePath == parentPath).Identity;
            PhysicalObjectDeletion.DeleteVerified(new(target, parentIdentity, identity, entry.Kind, entry.SizeBytes, entry.Sha256));
        }
    }

    internal static void RequireRemaining(string path, ArchivedFileTree expected)
    {
        var actual = new FileTreeScanner(SnapshotBudget.Default).Capture(path, default);
        if (actual.Shape == FileObjectKind.Missing) return;
        PhysicalTreeIdentity.RequireSingleLinks(path, actual);
        if (actual.Entries.Any(entry => !expected.Snapshot.Entries.Contains(entry)) ||
            PhysicalTreeIdentity.Capture(path, actual).Any(item => !expected.OriginalObjects.Contains(item)))
            throw InstallationIdentityCodec.Invalid();
    }
}
