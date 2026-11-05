using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record LogCatalogEntry(LogCatalogItem Item, BlobReference Raw);
internal sealed record LogCatalogSnapshot(AttemptReceipt Receipt, long TerminalRevision, string TerminalSha256,
    IReadOnlyList<LogCatalogEntry> Entries)
{
    internal IReadOnlyDictionary<int, RecordContentAvailability> ContentAvailability { get; init; } =
        new Dictionary<int, RecordContentAvailability>();
}
