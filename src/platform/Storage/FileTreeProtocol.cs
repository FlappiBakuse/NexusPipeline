using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage;

internal static class FileTreeProtocol
{
    internal static FileTree ToWire(FileTreeSnapshot tree) => new()
    {
        Shape = tree.Shape.ToString().ToLowerInvariant(),
        Entries = tree.Entries.Select(entry => new FileTreeEntriesItem
        {
            RelativePath = entry.RelativePath, Kind = entry.Kind.ToString().ToLowerInvariant(), SizeBytes = entry.SizeBytes, Sha256 = entry.Sha256
        }).ToArray()
    };

    internal static ArchivedFileTree FromWire(FileTree tree, IReadOnlyList<PhysicalTreeObject> identities)
    {
        var entries = Array.AsReadOnly(tree.Entries.Select(item => new FileTreeEntry(item.RelativePath,
            Enum.Parse<FileObjectKind>(item.Kind, true), item.SizeBytes, item.Sha256)).ToArray());
        var contents = Array.AsReadOnly(entries.Where(item => item.Kind == FileObjectKind.File).Select(item =>
            new BlobReference($"blobs/content/{item.Sha256}.bin", item.SizeBytes, item.Sha256!)).Distinct().ToArray());
        return new(new(Enum.Parse<FileObjectKind>(tree.Shape, true), entries), identities, contents);
    }

    internal static FileTreePhysicalIdentity ToWire(PhysicalObjectIdentity identity) => new()
        { VolumeSerial = identity.VolumeSerial, FileId = identity.FileId };
    internal static PhysicalObjectIdentity FromWire(FileTreePhysicalIdentity identity) => new(identity.VolumeSerial, identity.FileId);
}
