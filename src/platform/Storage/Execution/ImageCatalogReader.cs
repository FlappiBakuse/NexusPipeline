using System.Runtime.Versioning;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Records;

namespace NexusPipeline.Platform.Storage.Execution;

public sealed class ImageCatalogReader(RuntimeInstance instance, StateDatabase database, ContractReader contracts) : IRunImageCatalog
{
    public ImageCatalogPage ReadPage(ImageCatalogQuery query)
    {
        ImageCatalogPolicy.Validate(query);
        var expected = Read(query.RunId, query.AttemptId);
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
        using (var transaction = DatabaseConnections.BeginRead(connection))
        {
            ImageCatalogRows.RequireSaved(connection, transaction, expected);
            transaction.Commit();
        }
        var start = Math.Min(query.AfterPosition + 1, expected.Entries.Count);
        var count = Math.Min(query.Limit, expected.Entries.Count - start);
        var more = start + count < expected.Entries.Count;
        var retirements = new VerifiedImageRetirement.ReadScope(instance, database, contracts, query.RunId);
        var journal = new Journals.JournalStore(instance, contracts).Read(expected.Receipt.JournalId);
        if (journal.Revision != expected.TerminalRevision || journal.Sha256 != expected.TerminalSha256)
            throw ImageCatalogRows.Invalid();
        var ordinals = new Dictionary<(string StepId, string JobName), int>();
        var items = new ImageCatalogItem[count];
        for (var index = 0; index < count; index++)
        {
            var entry = expected.Entries[start + index];
            var item = entry.Item;
            var binding = expected.Bindings[item.Key.CaptureId];
            var job = (item.Key.StepId, binding.Primary.JobName);
            if (!ordinals.TryGetValue(job, out var ordinal))
            {
                ordinal = ExecutionInvocationCodec.OrdinalForJob(journal.Root, job.StepId, job.JobName);
                ordinals.Add(job, ordinal);
            }
            items[index] = item with
            {
                InvocationOrdinal = ordinal,
                ContentAvailability = retirements.ForEntry(binding, entry) is null ? item.ContentAvailability : RecordContentAvailability.Retired
            };
        }
        return new(Array.AsReadOnly(items), more ? items[^1].Position : null);
    }

    [SupportedOSPlatform("windows")]
    public Stream OpenOriginal(RunImageKey key, string resultSha256)
    {
        ImageCatalogPolicy.Validate(key);
        if (!NexusPipeline.Core.Configuration.FileTreePolicy.Digest(resultSha256)) throw ImageCatalogRows.Invalid();
        var expected = Read(key.RunId, key.AttemptId, original: true);
        var entry = expected.Entries.SingleOrDefault(value => value.Item.Key == key && value.Result.Sha256 == resultSha256);
        if (entry is not { Png: not null } || entry.Item.Outcome != RunImageOutcome.Captured) throw ImageCatalogRows.Invalid();
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
        using (var transaction = DatabaseConnections.BeginRead(connection))
        { ImageCatalogRows.RequireSaved(connection, transaction, expected); transaction.Commit(); }
        return new CaptureEvidenceReader(instance, database, contracts)
            .OpenOriginalCaptured(expected.Bindings[key.CaptureId], entry.Result, key.CaptureId,
                expected.ValidatedRead ?? throw ImageCatalogRows.Invalid());
    }

    private ImageCatalogSnapshot Read(Guid runId, Guid attemptId, bool original = false)
    {
        var receipt = new AttemptReceiptStore(database).Read(attemptId) ?? throw ImageCatalogRows.Invalid();
        if (receipt.Facts.RunId != runId) throw ImageCatalogRows.Invalid();
        var capture = new ImageCatalogCapture(instance, database, contracts);
        return original ? capture.Read(receipt) : capture.ReadMetadata(receipt);
    }
}
