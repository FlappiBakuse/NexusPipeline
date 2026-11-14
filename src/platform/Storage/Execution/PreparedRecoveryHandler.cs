using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

public sealed class PreparedRecoveryHandler(RuntimeInstance instance, ContractReader contracts, StateDatabase database,
    Func<Guid, AttemptWriterObservation> writers, IAttemptPersistenceObserver? observer = null, InstanceLease? executionLease = null) : IJournalRecoveryHandler
{
    public JournalKind Kind => JournalKind.RunAttempt;

    public IJournalRecoveryWork Prepare(RegisteredJournal journal)
    {
        if (database.InstanceId != instance.InstanceId || journal.Kind != Kind) throw new IOException("attempt_recovery_identity_unbound");
        var frozen = journal with { ResourceKeys = Array.AsReadOnly(journal.ResourceKeys.ToArray()) };
        Verify(frozen);
        return new Work(() =>
        {
            Verify(frozen);
            if (!Directory.Exists(JournalPath(frozen.JournalId)))
                new RunAttemptAcceptance(instance, contracts, database, observer).MaterializeInitial(frozen, AttemptPreparedBinding.ReadPlan(instance, contracts, frozen));
            ExecutionCancellationStore.ResumeAccepted(instance, database, contracts, frozen.JournalId, executionLease);
            var state = AttemptWriterState.Read(instance, database, contracts, frozen.JournalId);
            if (state.IsExecutionStart && state.Gate.State == EffectGateState.Entered)
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                new AttemptTerminalStore(instance, database, contracts,
                    executionLease ?? throw new IOException("runtime_exclusive_lease_required")).ReconcileNonterminal(frozen.JournalId);
                return new EnteredAttemptRecovery(new EnteredRecoverySession(instance, database, contracts,
                    executionLease ?? throw new IOException("runtime_exclusive_lease_required"), frozen.JournalId, () => writers(frozen.JournalId))).Recover();
            }
            if (state.Cancellation?.ReasonCode == DispatchIntentCodec.Reason)
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                var terminal = new AttemptTerminalStore(instance, database, contracts,
                    executionLease ?? throw new IOException("runtime_exclusive_lease_required"));
                if (state.Journal.Phase == JournalPhase.Committed) { _ = terminal.CommitPreparedCancellation(frozen.JournalId); return JournalRecoveryResult.AlreadyCompleted; }
                terminal.PrepareCancellation(frozen.JournalId);
                if (HasConfigurationGroups(frozen.JournalId))
                {
                    _ = ConfigurationCompletionBinding.Read(instance, database, contracts, frozen.JournalId);
                    return JournalRecoveryResult.PendingConfigurationCompletion;
                }
                _ = terminal.CommitPreparedCancellation(frozen.JournalId); return JournalRecoveryResult.NotExecuted;
            }
            if (state.IsExecutionStart && state.Cancellation?.ReasonCode is DispatchIntentCodec.InterruptionReason or DispatchIntentCodec.BudgetReason or DispatchIntentCodec.TargetReason)
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                var terminal = new AttemptTerminalStore(instance, database, contracts,
                    executionLease ?? throw new IOException("runtime_exclusive_lease_required"));
                var reason = state.Cancellation.ReasonCode;
                var lifecycle = reason == DispatchIntentCodec.BudgetReason ? LifecycleOutcome.Blocked : LifecycleOutcome.Interrupted;
                if (state.Journal.Phase == JournalPhase.Committed)
                { _ = terminal.CommitPreparedStop(frozen.JournalId, lifecycle, reason); return JournalRecoveryResult.AlreadyCompleted; }
                terminal.PrepareStopped(frozen.JournalId, lifecycle, reason);
                if (HasConfigurationGroups(frozen.JournalId))
                {
                    _ = ConfigurationCompletionBinding.Read(instance, database, contracts, frozen.JournalId);
                    return JournalRecoveryResult.PendingConfigurationCompletion;
                }
                _ = terminal.CommitPreparedStop(frozen.JournalId, lifecycle, reason); return JournalRecoveryResult.NotExecuted;
            }
            return new PreparedAttemptRecovery(new PreparedRecoverySession(instance, frozen.JournalId, contracts, database,
                () => writers(frozen.JournalId), observer)).Recover();
        });
    }

    private void Verify(RegisteredJournal expected)
    {
        var snapshot = new RecoveryJournalRegistry(database).ReadSnapshot(expected.JournalId);
        RecoveryRegistryPolicy.Verify(snapshot);
        var actual = snapshot.Journals.Single(item => item.JournalId == expected.JournalId);
        if (actual.OperationId != expected.OperationId || actual.Kind != expected.Kind || actual.Phase != expected.Phase || actual.Revision != expected.Revision ||
            actual.PlanPath != expected.PlanPath || actual.PlanDigest != expected.PlanDigest || !actual.ResourceKeys.SequenceEqual(expected.ResourceKeys))
            throw new IOException("attempt_registration_changed_after_preflight");
        if (Directory.Exists(JournalPath(expected.JournalId)))
        {
            _ = AttemptPreparedBinding.Read(instance, contracts, database, expected.JournalId);
            return;
        }
        if (File.Exists(JournalPath(expected.JournalId)) || expected.Phase != JournalPhase.Prepared || expected.Revision != 1 ||
            snapshot.FileEffects.Any(item => item.JournalId == expected.JournalId)) throw new IOException("missing_attempt_journal_not_initial");
        _ = AttemptPreparedBinding.ReadPlan(instance, contracts, expected);
        var gate = new JournalEffectGate(database).Read(expected.JournalId);
        if (gate.Kind != Kind || gate.OperationId != expected.OperationId || gate.PlanDigest != expected.PlanDigest) throw new IOException("missing_attempt_journal_gate_unbound");
    }

    private string JournalPath(Guid id) => PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "journals", id.ToString("D")));
    private bool HasConfigurationGroups(Guid journalId)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT EXISTS(SELECT 1 FROM configuration.configuration_run_sessions WHERE parent_journal_id=$parent);";
        command.Parameters.AddWithValue("$parent", journalId.ToString("D")); return (long)command.ExecuteScalar()! != 0;
    }
    private sealed class Work(Func<JournalRecoveryResult> execute) : IJournalRecoveryWork
    {
        public JournalRecoveryResult Execute() => execute();
    }
}
