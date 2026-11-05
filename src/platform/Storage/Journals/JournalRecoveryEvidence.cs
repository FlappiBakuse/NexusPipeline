using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed class JournalRecoveryEvidence(RuntimeInstance instance, JournalStore journals, StateDatabase? database = null) : IRecoveryEvidence, IRecoveryPreflight
{
    public void VerifyRecognizedAll(IReadOnlyList<RegisteredJournal> registered)
    {
        VerifyRegisteredDirectory(registered, allowMissingPrepared: true);
        VerifyRecognizedScope(registered);
    }

    internal void VerifyRecognizedScope(IReadOnlyList<RegisteredJournal> registered)
    {
        foreach (var journal in registered)
        {
            if (!Directory.Exists(Path.Combine(instance.RuntimeRoot, "journals", journal.JournalId.ToString("D"))))
            {
                journals.VerifyReferencedPlan(journal);
                continue;
            }
            var revisions = journals.ReadAllRevisions(journal.JournalId);
            var projected = revisions.SingleOrDefault(item => item.Revision == journal.Revision)
                ?? throw new IOException("registered_journal_revision_missing");
            VerifyAgreement(journal, projected);
        }
    }

    public void VerifyAll(IReadOnlyList<RegisteredJournal> registered)
    {
        VerifyRegisteredDirectory(registered);
        foreach (var journal in registered)
        {
            var physical = journals.Read(journal.JournalId);
            VerifyAgreement(journal, physical);
        }
    }

    private void VerifyRegisteredDirectory(IReadOnlyList<RegisteredJournal> registered, bool allowMissingPrepared = false)
    {
        var root = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "journals"));
        if (File.Exists(root)) throw new IOException("journal_root_shape_unrecognized");
        if (!Directory.Exists(root))
        {
            if (registered.Any(item => !allowMissingPrepared || item.Phase != JournalPhase.Prepared || item.Revision != 1))
                throw new IOException("registered_journal_missing");
            return;
        }
        var expected = registered.Select(item => item.JournalId.ToString("D")).ToHashSet(StringComparer.Ordinal);
        var history = database is null ? null : new RecoveryJournalRegistry(database).ReadInactiveIds();
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            PhysicalPathPolicy.NormalizeWithoutLinks(entry);
            var name = Path.GetFileName(entry);
            if (!Directory.Exists(entry)) throw new IOException("journal_directory_unknown_entry");
            if (expected.Remove(name)) continue;
            if (history is null || !Guid.TryParseExact(name, "D", out var id) || id.ToString("D") != name ||
                !history.Contains(id))
                throw new IOException("journal_directory_unknown_entry");
        }
        if (registered.Any(item => expected.Contains(item.JournalId.ToString("D")) &&
            (!allowMissingPrepared || item.Phase != JournalPhase.Prepared || item.Revision != 1)))
            throw new IOException("registered_journal_missing");
    }

    private static void VerifyAgreement(RegisteredJournal registered, JournalDocument physical)
    {
        var root = physical.Root;
        var resources = root.GetProperty("resourceKeys").EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal);
        if (registered.OperationId != root.GetProperty("operationId").GetGuid() || registered.Kind != physical.Kind || registered.Phase != physical.Phase ||
            registered.Revision != physical.Revision || registered.PlanPath != physical.PlanPath || registered.PlanDigest != physical.PlanDigest ||
            !registered.ResourceKeys.SequenceEqual(resources))
            throw new IOException("journal_registry_reconciliation_required");
    }
}
