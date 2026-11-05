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
        var selected = new List<ImageCatalogItem>();
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            ImageCatalogRows.RequireSaved(connection, transaction, expected);
            using var command = ImageCatalogRows.PageCommand(connection, transaction, query);
            using var rows = command.ExecuteReader();
            while (rows.Read()) selected.Add(ImageCatalogRows.ReadEntry(rows, query.RunId, query.AttemptId).Item);
            transaction.Commit();
        }
        var more = selected.Count > query.Limit;
        if (more) selected.RemoveAt(selected.Count - 1);
        var retirements = new VerifiedImageRetirement.ReadScope(instance, database, contracts, query.RunId);
        var entries = expected.Entries.ToDictionary(value => value.Item.Key);
        var journal = new Journals.JournalStore(instance, contracts).Read(expected.Receipt.JournalId);
        if (journal.Revision != expected.TerminalRevision || Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(journal.Export())) != expected.TerminalSha256)
            throw ImageCatalogRows.Invalid();
        var items = selected.Select(item =>
        {
            var entry = entries[item.Key];
            var binding = expected.Bindings[item.Key.CaptureId];
            return item with
            {
                InvocationOrdinal = ExecutionInvocationCodec.OrdinalForJob(journal.Root, item.Key.StepId, binding.Primary.JobName),
                ContentAvailability = retirements.ForEntry(binding, entry) is null ? item.ContentAvailability : RecordContentAvailability.Retired
            };
        }).ToArray();
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
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        { ImageCatalogRows.RequireSaved(connection, transaction, expected); transaction.Commit(); }
        return new CaptureEvidenceReader(instance, database, contracts)
            .OpenOriginalCaptured(expected.Bindings[key.CaptureId], entry.Result, key.CaptureId,
                expected.ValidatedRead ?? throw ImageCatalogRows.Invalid());
    }

    public IReadOnlyList<string> ExplainPage(ImageCatalogQuery query)
    {
        ImageCatalogPolicy.Validate(query);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = ImageCatalogRows.PageCommand(connection, transaction, query);
        command.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
        var lines = new List<string>();
        using var rows = command.ExecuteReader(); while (rows.Read()) lines.Add(rows.GetString(3));
        return lines.AsReadOnly();
    }

    private ImageCatalogSnapshot Read(Guid runId, Guid attemptId, bool original = false)
    {
        var receipt = new AttemptReceiptStore(database).Read(attemptId) ?? throw ImageCatalogRows.Invalid();
        if (receipt.Facts.RunId != runId) throw ImageCatalogRows.Invalid();
        var capture = new ImageCatalogCapture(instance, database, contracts);
        return original ? capture.Read(receipt) : capture.ReadMetadata(receipt);
    }
}
