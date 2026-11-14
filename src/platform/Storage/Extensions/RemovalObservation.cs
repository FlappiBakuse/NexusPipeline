using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record RemovalObservation(ArchivedFileTree Before, string? Reason)
{
    internal static RemovalObservation Read(PayloadAction action, bool external, string target,
        ExtensionInstalledFootprint? original, ImmutableBlobStore blobs, CancellationToken cancellation)
    {
        var missing = new ArchivedFileTree(new(FileObjectKind.Missing, []), [], []);
        if (external) return new(missing, "external-removal");
        var before = original?.Payload ?? missing;
        if (action != PayloadAction.Remove || original is null) return new(before, null);
        try
        {
            using var held = TreeReadLease.Acquire(target, original.ParentIdentity, before);
            return new(before, null);
        }
        catch (IOException) when (Directory.Exists(target) && PhysicalObjectIdentity.Read(target) == before.OriginalIdentity)
        {
            var observed = new FileTreeArchive(new(new(4096, 268435456)), blobs).Capture(target, cancellation);
            return new(observed, "files-changed");
        }
    }
}
