using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed class CleanupRecoveryHandler(RuntimeInstance instance, ContractReader contracts, StateDatabase database,
    Func<Guid, CleanupWriterObservation> writers, ICleanupPersistenceObserver? observer = null, PayloadApplyRoots? payloadRoots = null,
    InstanceLease? executionLease = null, JournalRecoveryOwnership? recoveryOwnership = null) : IJournalRecoveryHandler
{
    public JournalKind Kind => JournalKind.FileCleanup;

    public IJournalRecoveryWork Prepare(RegisteredJournal journal)
    {
        var frozen = journal with { ResourceKeys = Array.AsReadOnly(journal.ResourceKeys.ToArray()) };
        Verify(frozen);
        return new Work(() =>
        {
            Verify(frozen);
            var path = JournalPath(frozen.JournalId);
            if (!Directory.Exists(path)) new FileCleanupAcceptance(instance, contracts, database).MaterializeInitial(frozen);
            var plan = CleanupJournalBinding.ReadPlan(instance, contracts, frozen, database, payloadRoots);
            ExtensionWorkExecution? work = null;
            if (plan.ExtensionWork is { } origin && frozen.Phase != JournalPhase.Committed && executionLease is not null && recoveryOwnership is not null)
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                work = ExtensionWorkExecution.Recovery(instance, database, executionLease, contracts,
                    payloadRoots ?? throw new IOException("extension_work_roots_required"), origin, recoveryOwnership, new(frozen.PlanPath,
                        new FileInfo(Path.Combine(instance.RuntimeRoot, frozen.PlanPath)).Length, frozen.PlanDigest));
            }
            return new(instance, frozen.JournalId, contracts, database, () => writers(frozen.JournalId), observer, payloadRoots, work);
        });
    }

    private void Verify(RegisteredJournal expected)
    {
        if (database.InstanceId != instance.InstanceId) throw new IOException("cleanup_recovery_instance_unbound");
        var snapshot = new RecoveryJournalRegistry(database).ReadSnapshot(expected.JournalId);
        var actual = snapshot.Journals.Single(item => item.JournalId == expected.JournalId);
        if (actual.OperationId != expected.OperationId || actual.Kind != expected.Kind || actual.Phase != expected.Phase || actual.Revision != expected.Revision ||
            actual.PlanPath != expected.PlanPath || actual.PlanDigest != expected.PlanDigest || !actual.ResourceKeys.SequenceEqual(expected.ResourceKeys))
            throw new IOException("cleanup_recovery_registration_changed");
        var path = JournalPath(expected.JournalId);
        if (Directory.Exists(path)) { _ = CleanupJournalBinding.Read(instance, contracts, snapshot, expected.JournalId, database, payloadRoots); return; }
        if (File.Exists(path) || expected.Phase != JournalPhase.Prepared || expected.Revision != 1)
            throw new IOException("missing_cleanup_journal_not_initial");
        var plan = CleanupJournalBinding.ReadPlan(instance, contracts, expected, database, payloadRoots);
        var effects = snapshot.FileEffects.Where(item => item.JournalId == expected.JournalId).ToArray();
        if (effects.Length != FileCleanupItems.Steps(plan).Count || effects.Any(effect => effect.State != FileEffectState.Pending ||
            effect.Action != FileEffectAction.RemoveOwned || effect.IntentDigest != expected.PlanDigest || !FileCleanupItems.Steps(plan).Contains(effect.StepId)))
            throw new IOException("missing_cleanup_journal_effects_unbound");
    }

    private string JournalPath(Guid journalId) => PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "journals", journalId.ToString("D")));

    private sealed class Work(Func<CleanupRecoverySession> open) : IJournalRecoveryWork
    {
        public JournalRecoveryResult Execute() => new FileCleanupRecovery(open()).Recover() switch
        {
            FileCleanupResult.NotExecuted => JournalRecoveryResult.NotExecuted,
            FileCleanupResult.Cleaned => JournalRecoveryResult.Completed,
            FileCleanupResult.Quarantined => JournalRecoveryResult.Quarantined,
            FileCleanupResult.AlreadyCompleted => JournalRecoveryResult.AlreadyCompleted,
            _ => throw new InvalidOperationException("cleanup_result_unrecognized")
        };
    }
}

