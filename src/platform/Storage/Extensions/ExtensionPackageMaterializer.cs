using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Extensions;

internal static class ExtensionPackageMaterializer
{
    internal static void Materialize(ImmutableBlobStore blobs, FrozenPayloadApply plan, string requestedDestination,
        CancellationToken cancellation)
    {
        if (plan.Kind != JournalKind.ExtensionApply || plan.Action == PayloadAction.Remove) throw Invalid();
        var codec = new PayloadSourceCodec(blobs.Instance, new());
        var source = codec.ReadMetadata(plan.ExtensionSource ?? throw Invalid());
        codec.RequireFrozenBinding(source, plan);
        var package = source.Package ?? throw Invalid();
        using var input = blobs.OpenVerified(package.Archive);
        using var archive = ExtensionPackageArchive.OpenFrozen(input, plan.Candidate.Snapshot, cancellation);
        var candidate = archive.CaptureCandidate(blobs, cancellation);
        if (candidate.Snapshot.Shape != plan.Candidate.Snapshot.Shape ||
            !candidate.Snapshot.Entries.SequenceEqual(plan.Candidate.Snapshot.Entries)) throw Invalid();
        var destination = PhysicalPathPolicy.NormalizeWithoutLinks(requestedDestination);
        if (Path.Exists(destination)) throw Invalid();
        foreach (var entry in candidate.Snapshot.Entries.OrderBy(item => item.RelativePath.Length).ThenBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            var path = entry.RelativePath.Length == 0 ? destination : Path.Combine(destination, entry.RelativePath);
            if (entry.Kind == FileObjectKind.Directory) ExclusiveDirectory.Create(path);
            else
            {
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
                archive.CopyFile(entry.RelativePath, output, cancellation);
                output.Flush(flushToDisk: true);
            }
        }
    }

    private static IOException Invalid() => new("extension_package_materialization_unbound");
}
