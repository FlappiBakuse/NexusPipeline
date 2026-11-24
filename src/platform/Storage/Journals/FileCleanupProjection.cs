using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Platform.Storage.Journals;

internal sealed class FileCleanupProjection(JournalStore store, RecoveryJournalRegistry registry, FileEffectOutbox outbox,
    ICleanupPersistenceObserver? observer, StateDatabase? database = null, ExtensionWorkExecution? work = null)
{
    internal void Synchronize(FrozenFileCleanup plan, bool verifiedRecovery, JournalDocument? justAppended = null)
    {
        if (plan.ExtensionWork is not null) { SynchronizeWork(plan, verifiedRecovery, justAppended); return; }
        var registered = registry.ReadJournal(plan.JournalId);
        foreach (var next in PendingRevisions(plan.JournalId, registered.Revision, justAppended))
        {
            var receipts = LatestReceipts(next);
            var noEffects = receipts.ContainsKey("verify-no-effect");
            foreach (var effect in outbox.Read(plan.JournalId))
            {
                var desired = DesiredEffect(receipts, effect.StepId, noEffects);
                if (effect.State == desired || effect.State is FileEffectState.Completed or FileEffectState.NotExecuted) continue;
                outbox.Record(plan.JournalId, effect.StepId, effect.State, desired, next.Sha256,
                    desired == FileEffectState.NotExecuted ? new(true, false, false, false) : null);
                observer?.Reached(CleanupPersistencePoint.OutboxCommitted, plan.JournalId, next.Revision, next.Phase);
            }
            registry.Database.Write((connection, transaction) =>
            {
                registry.AdvanceInside(connection, transaction, registered with { Revision = next.Revision, Phase = next.Phase },
                    next.Revision - 1, noEffects ? new(true, false, false, false) : null, verifiedRecovery);
                if (next.Phase == JournalPhase.Committed)
                {
                    using var command = connection.CreateCommand(); command.Transaction = transaction;
                    command.CommandText = "UPDATE main.operations SET status='completed' WHERE initial_journal_id=$journal " +
                        "AND operation_id=$operation AND operation_kind IN ($kind,$content) AND status='accepted';";
                    command.Parameters.AddWithValue("$journal", plan.JournalId.ToString("D"));
                    command.Parameters.AddWithValue("$operation", plan.OperationId.ToString("D"));
                    command.Parameters.AddWithValue("$kind", ExtensionArchiveCleanup.OperationKind);
                    command.Parameters.AddWithValue("$content", ExtensionArchiveCleanup.ContentOperationKind);
                    command.ExecuteNonQuery();
                }
                return true;
            });
            observer?.Reached(CleanupPersistencePoint.SqlProjectionCommitted, plan.JournalId, next.Revision, next.Phase);
        }
    }
    private void SynchronizeWork(FrozenFileCleanup plan, bool verifiedRecovery, JournalDocument? justAppended)
    {
        if (database is null || work is null) throw new IOException("extension_work_projection_authority_required");
        var registered = registry.ReadJournal(plan.JournalId);
        foreach (var next in PendingRevisions(plan.JournalId, registered.Revision, justAppended))
        {
            var receipts = LatestReceipts(next);
            var noEffects = receipts.ContainsKey("verify-no-effect");
            database.Write((connection, transaction) =>
            {
                work.RequireInside(connection, transaction);
                var current = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, plan.JournalId);
                if (current.Revision != next.Revision - 1 || current.PlanDigest != registered.PlanDigest ||
                    current.PlanPath != registered.PlanPath || !current.ResourceKeys.SequenceEqual(registered.ResourceKeys))
                    throw new IOException("extension_work_projection_cursor_changed");
                foreach (var effect in FileEffectRows.Read(connection, plan.JournalId, transaction))
                {
                    var desired = DesiredEffect(receipts, effect.StepId, noEffects);
                    if (effect.State == desired || effect.State is FileEffectState.Completed or FileEffectState.NotExecuted) continue;
                    FileEffectOutbox.RecordInside(connection, transaction, plan.JournalId, effect.StepId, effect.State, desired, next.Sha256,
                        desired == FileEffectState.NotExecuted ? new(true, false, false, false) : null);
                }
                registry.AdvanceInside(connection, transaction, current with { Revision = next.Revision, Phase = next.Phase },
                    current.Revision, noEffects ? new(true, false, false, false) : null, verifiedRecovery);
                if (next.Phase == JournalPhase.Committed)
                {
                    using var command = connection.CreateCommand(); command.Transaction = transaction;
                    command.CommandText = "UPDATE main.operations SET status='completed' WHERE operation_id=$operation " +
                        "AND operation_kind='file.cleanup' AND status='accepted' AND initial_journal_id=$journal;";
                    command.Parameters.AddWithValue("$operation", plan.OperationId.ToString("D"));
                    command.Parameters.AddWithValue("$journal", plan.JournalId.ToString("D"));
                    if (command.ExecuteNonQuery() != 1) throw new IOException("extension_work_operation_unbound");
                }
                return true;
            });
            if (next.Phase is JournalPhase.Committed or JournalPhase.Quarantined) work.ReleaseProtectedEvidence();
            observer?.Reached(CleanupPersistencePoint.OutboxCommitted, plan.JournalId, next.Revision, next.Phase);
            observer?.Reached(CleanupPersistencePoint.SqlProjectionCommitted, plan.JournalId, next.Revision, next.Phase);
        }
    }

    private IEnumerable<JournalDocument> PendingRevisions(Guid journalId, long revision, JournalDocument? justAppended)
    {
        if (justAppended is not null && justAppended.JournalId == journalId && justAppended.Revision == revision + 1 &&
            store.HasProtectedHead(justAppended)) return [justAppended];
        return store.ReadAllRevisions(journalId).Where(item => item.Revision > revision);
    }

    private static Dictionary<string, JsonElement> LatestReceipts(JournalDocument document)
    {
        var receipts = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var receipt in document.Root.GetProperty("receipts").EnumerateArray())
            receipts[receipt.GetProperty("stepId").GetString()!] = receipt;
        return receipts;
    }

    private static FileEffectState DesiredEffect(IReadOnlyDictionary<string, JsonElement> receipts, string step, bool noEffects)
    {
        if (noEffects) return FileEffectState.NotExecuted;
        if (!receipts.TryGetValue(step, out var receipt)) return FileEffectState.Pending;
        return receipt.GetProperty("effect").GetString() switch
        {
            "started" => FileEffectState.Started,
            "completed" => FileEffectState.Completed,
            "notExecuted" => FileEffectState.NotExecuted,
            _ => throw new IOException("cleanup_projection_effect_unknown")
        };
    }
}

