using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Platform.Storage.Journals;

internal sealed class FileCleanupProjection(JournalStore store, RecoveryJournalRegistry registry, FileEffectOutbox outbox,
    ICleanupPersistenceObserver? observer, StateDatabase? database = null, ExtensionWorkExecution? work = null)
{
    internal void Synchronize(FrozenFileCleanup plan, bool verifiedRecovery)
    {
        if (plan.ExtensionWork is not null) { SynchronizeWork(plan, verifiedRecovery); return; }
        var registered = registry.ReadJournal(plan.JournalId);
        foreach (var next in store.ReadAllRevisions(plan.JournalId).Where(item => item.Revision > registered.Revision))
        {
            var receipts = next.Root.GetProperty("receipts").EnumerateArray().ToArray();
            var noEffects = receipts.Any(item => item.GetProperty("stepId").GetString() == "verify-no-effect");
            var digest = Convert.ToHexStringLower(SHA256.HashData(next.Export()));
            foreach (var effect in outbox.Read(plan.JournalId))
            {
                var receipt = receipts.LastOrDefault(item => item.GetProperty("stepId").GetString() == effect.StepId);
                var desired = noEffects ? FileEffectState.NotExecuted : receipt.ValueKind == System.Text.Json.JsonValueKind.Undefined ? FileEffectState.Pending :
                    receipt.GetProperty("effect").GetString() switch
                    {
                        "started" => FileEffectState.Started, "completed" => FileEffectState.Completed, "notExecuted" => FileEffectState.NotExecuted,
                        _ => throw new IOException("cleanup_projection_effect_unknown")
                    };
                if (effect.State == desired || effect.State is FileEffectState.Completed or FileEffectState.NotExecuted) continue;
                outbox.Record(plan.JournalId, effect.StepId, effect.State, desired, digest,
                    desired == FileEffectState.NotExecuted ? new(true, false, false, false) : null);
                observer?.Reached(CleanupPersistencePoint.OutboxCommitted, plan.JournalId, next.Revision, next.Phase);
            }
            registry.Advance(registered with { Revision = next.Revision, Phase = next.Phase }, next.Revision - 1,
                noEffects ? new(true, false, false, false) : null, verifiedRecovery);
            observer?.Reached(CleanupPersistencePoint.SqlProjectionCommitted, plan.JournalId, next.Revision, next.Phase);
        }
    }
    private void SynchronizeWork(FrozenFileCleanup plan, bool verifiedRecovery)
    {
        if (database is null || work is null) throw new IOException("extension_work_projection_authority_required");
        var registered = registry.ReadJournal(plan.JournalId);
        foreach (var next in store.ReadAllRevisions(plan.JournalId).Where(item => item.Revision > registered.Revision))
        {
            var receipts = next.Root.GetProperty("receipts").EnumerateArray().ToArray();
            var noEffects = receipts.Any(item => item.GetProperty("stepId").GetString() == "verify-no-effect");
            var digest = Convert.ToHexStringLower(SHA256.HashData(next.Export()));
            database.Write((connection, transaction) =>
            {
                work.RequireInside(connection, transaction);
                var current = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, plan.JournalId);
                if (current.Revision != next.Revision - 1 || current.PlanDigest != registered.PlanDigest ||
                    current.PlanPath != registered.PlanPath || !current.ResourceKeys.SequenceEqual(registered.ResourceKeys))
                    throw new IOException("extension_work_projection_cursor_changed");
                foreach (var effect in FileEffectRows.Read(connection, plan.JournalId, transaction))
                {
                    var receipt = receipts.LastOrDefault(item => item.GetProperty("stepId").GetString() == effect.StepId);
                    var desired = noEffects ? FileEffectState.NotExecuted : receipt.ValueKind == System.Text.Json.JsonValueKind.Undefined ? FileEffectState.Pending :
                        receipt.GetProperty("effect").GetString() switch
                        {
                            "started" => FileEffectState.Started, "completed" => FileEffectState.Completed, "notExecuted" => FileEffectState.NotExecuted,
                            _ => throw new IOException("cleanup_projection_effect_unknown")
                        };
                    if (effect.State == desired || effect.State is FileEffectState.Completed or FileEffectState.NotExecuted) continue;
                    FileEffectOutbox.RecordInside(connection, transaction, plan.JournalId, effect.StepId, effect.State, desired, digest,
                        desired == FileEffectState.NotExecuted ? new(true, false, false, false) : null);
                }
                registry.AdvanceInside(connection, transaction, current with { Revision = next.Revision, Phase = next.Phase },
                    current.Revision, noEffects ? new(true, false, false, false) : null, verifiedRecovery);
                return true;
            });
            if (next.Phase is JournalPhase.Committed or JournalPhase.Quarantined) work.ReleaseProtectedEvidence();
            observer?.Reached(CleanupPersistencePoint.OutboxCommitted, plan.JournalId, next.Revision, next.Phase);
            observer?.Reached(CleanupPersistencePoint.SqlProjectionCommitted, plan.JournalId, next.Revision, next.Phase);
        }
    }

}

