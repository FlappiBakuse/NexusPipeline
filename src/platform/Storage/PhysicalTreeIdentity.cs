using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage;

public sealed record PhysicalTreeObject(string RelativePath, PhysicalObjectIdentity Identity);

public static class PhysicalTreeIdentity
{
    internal static void RequireSingleLinks(string requestedRoot, FileTreeSnapshot snapshot)
    {
        var root = PhysicalPathPolicy.NormalizeWithoutLinks(requestedRoot);
        foreach (var entry in snapshot.Entries.Where(entry => entry.Kind == FileObjectKind.File))
            PhysicalObjectIdentity.RequireSingleLink(entry.RelativePath.Length == 0 ? root : Path.Combine(root, entry.RelativePath));
    }

    public static IReadOnlyList<PhysicalTreeObject> Capture(string requestedRoot, FileTreeSnapshot snapshot)
    {
        var root = PhysicalPathPolicy.NormalizeWithoutLinks(requestedRoot);
        return Array.AsReadOnly(snapshot.Entries.Select(entry => new PhysicalTreeObject(entry.RelativePath,
            PhysicalObjectIdentity.Read(entry.RelativePath.Length == 0 ? root : Path.Combine(root, entry.RelativePath)))).ToArray());
    }
}
