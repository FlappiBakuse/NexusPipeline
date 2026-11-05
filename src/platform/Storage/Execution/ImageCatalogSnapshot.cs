using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ImageCatalogEntry(ImageCatalogItem Item, BlobReference Source,
    BlobReference? Root, BlobReference Result, BlobReference? Png);
internal sealed record ImageCatalogSnapshot(AttemptReceipt Receipt, long TerminalRevision, string TerminalSha256,
    IReadOnlyList<ImageCatalogEntry> Entries)
{
    internal required FrozenStartSource StartSource { get; init; }
    internal required IReadOnlyDictionary<Guid, ExecutionCaptureBinding> Bindings { get; init; }
    internal AttemptValidatedRead? ValidatedRead { get; init; }
}
