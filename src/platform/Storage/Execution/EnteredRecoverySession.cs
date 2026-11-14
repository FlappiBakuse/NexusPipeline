using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class EnteredRecoverySession(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    InstanceLease lease, Guid parentJournalId, Func<AttemptWriterObservation> writers)
    : IEnteredRecoverySession, IEnteredConfigurationCompletion
{
    private readonly AttemptTerminalStore _store = new(instance, database, contracts, lease);

    public EnteredAttemptInspection Inspect()
    {
        var state = _store.Read(parentJournalId); var terminal = AttemptResultCodec.Read(state.Latest.Root, state.Attempt);
        var agrees = state.Journal.Revision == state.Latest.Revision && state.Journal.Phase == state.Latest.Phase;
        var committed = state.Latest.Phase == JournalPhase.Committed;
        if (committed && agrees && terminal is not null)
            return new(JournalPhase.Committed, true, state.Gate.State, true, WriterState.Stopped, true, true, true, terminal.Facts);
        var observed = Observe(); var collected = Collected(state); var physicalRestored = Restored(state, completeGroups: false);
        var complete = physicalRestored && Restored(state, completeGroups: true);
        var facts = terminal?.Facts ?? ActiveFacts(state);
        return new(state.Latest.Phase, agrees, state.Gate.State, observed.IsComplete, observed.State, collected,
            state.Latest.Phase == JournalPhase.Quarantined ? physicalRestored : complete,
            committed && collected && complete, facts)
        {
            CanDeferConfigurationCompletion = !committed && state.Latest.Phase == JournalPhase.Applied && collected &&
                state.Attempt.ConfigurationPlans.Count != 0 && !complete
        };
    }

    public void FreezeDispatch()
    {
        var state = _store.Read(parentJournalId);
        if (state.Latest.Revision != state.Journal.Revision && state.Latest.Phase != JournalPhase.Committed)
        {
            if (Observe() is not { IsComplete: true, State: WriterState.Stopped })
                throw new IOException("attempt_writer_stop_unconfirmed");
            _store.ReconcileNonterminal(parentJournalId); state = _store.Read(parentJournalId);
        }
        if (!(state.Latest.Phase == JournalPhase.Quarantined && state.IsDispatchClosed))
        {
            _store.FreezeDispatch(parentJournalId, interrupted: true);
            if (_store.Read(parentJournalId).Latest.Phase == JournalPhase.Prepared) _store.EnsureApplying(parentJournalId);
        }
        _store.CloseUndispatched(parentJournalId);
        if (NecessaryCollected(_store.Read(parentJournalId)) && Observe() is { IsComplete: true, State: WriterState.Stopped } && ReconcileTargets())
            CloseStoppedGroups();
    }

    public void StopWriters() { _ = Observe(); }
    public void CollectWorkers()
    {
        var state = _store.Read(parentJournalId);
        if (!AndroidCollected(state)) return;
        PreviewDomainRecovery.Reconcile(instance, database, contracts, lease, parentJournalId);
        state = _store.Read(parentJournalId);
        AttemptTerminalEvidence.RequireArtifacts(instance, database, contracts, state);
        var factory = new StepEvidenceFactory(instance, database, contracts, lease);
        foreach (var hook in state.Jobs.Where(job => ExecutionBindingRoles.IsHook(job.Role)))
        {
            state = _store.Read(parentJournalId);
            if (HookResultCodec.Read(state.Latest.Root, state.Total).Any(item => item.StepId == hook.StepId && item.Role == hook.Role)) continue;
            try
            {
                _store.RecordHookCollected(parentJournalId, PreparedHookResult.Interrupted(instance, database, contracts, lease,
                    StepResultCodec.Binding(state.Latest.Root, hook), hook.Role));
            }
            catch (IOException) { return; }
        }
        foreach (var step in state.Steps)
        {
            state = _store.Read(parentJournalId);
            if (StepResultCodec.Read(state.Latest.Root, state.Total).Any(item => item.Binding.StepId == step.StepId && item.IsCollected)) continue;
            try
            {
                var proof = factory.CreateInterruptedCollected(StepResultCodec.Binding(state.Latest.Root, step));
                _store.RecordCollected(parentJournalId, proof);
            }
            catch (IOException)
            {
                // A missing native closure cannot be replaced by a fabricated output completion.
                return;
            }
        }
        if (ReconcileTargets()) CloseStoppedGroups();
    }
    private bool ReconcileTargets()
    {
        try
        {
            PreviewDomainRecovery.Reconcile(instance, database, contracts, lease, parentJournalId);
            ExecutionCaptureRecovery.Reconcile(instance, database, contracts, lease, parentJournalId);
            ExecutionTargetRecovery.Reconcile(instance, database, contracts, lease, parentJournalId);
            ProviderLauncherRecovery.Reconcile(instance, database, contracts, lease, parentJournalId);
            return Collected(_store.Read(parentJournalId));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private void CloseStoppedGroups()
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT s.session_id,o.subject_id FROM configuration.configuration_run_sessions s JOIN main.operations o ON o.operation_id=s.session_id " +
            "WHERE s.parent_journal_id=$parent AND s.writer_gate=0 LIMIT 4097;";
        command.Parameters.AddWithValue("$parent", parentJournalId.ToString("D"));
        var groups = new List<(Guid Id, string Subject)>();
        using (var rows = command.ExecuteReader())
            while (rows.Read()) groups.Add((Guid.Parse(rows.GetString(0)), rows.GetString(1)));
        if (groups.Count > 4096) throw new IOException("configuration_run_inventory_limit");
        foreach (var group in groups)
            lease.PublishExecution(() =>
            {
                var session = new ConfigurationRunStore(instance, database, contracts).Read(group.Id, group.Subject)
                    ?? throw new IOException("configuration_run_missing");
                var state = _store.Read(parentJournalId);
                if (state.Total.Steps.Single(step => step.StepId == session.StepId).Hooks is { Count: > 0 } &&
                    state.Jobs.Any(job => job.StepId == session.StepId))
                    new WriterAdmissionStore(instance, database, contracts).CloseGroupAdmission(session);
            });
    }
    public void MarkApplied() => _store.MarkApplied(parentJournalId);
    public void RestoreConfiguration()
    {
        if (!Restored(_store.Read(parentJournalId), completeGroups: false)) return;
        AttemptTerminalEvidence.RequireStopped(database, parentJournalId);
    }
    public void VerifyPendingCompletion()
    {
        var state = _store.Read(parentJournalId);
        if (state.Latest.Phase != JournalPhase.Applied || state.Latest.Revision != state.Journal.Revision)
            throw new IOException("attempt_pending_configuration_completion_unverified");
        _ = ConfigurationCompletionBinding.Read(instance, database, contracts, parentJournalId);
    }
    public void CommitInterrupted(AttemptFacts facts)
    {
        var state = _store.Read(parentJournalId);
        var expected = ActiveFacts(state).AfterInterruption(WriterState.Stopped, true);
        if (facts != expected) throw new IOException("execution_terminal_facts_unbound");
        if (_store.Commit(parentJournalId, LifecycleOutcome.Interrupted, "host_interrupted").Facts != facts)
            throw new IOException("execution_terminal_facts_unbound");
    }
    public void Quarantine(AttemptFacts facts)
    {
        var previous = ActiveFacts(_store.Read(parentJournalId));
        if (facts != (previous with { Recovery = RecoveryOutcome.Quarantined, ReasonCode = facts.ReasonCode }))
            throw new IOException("execution_terminal_facts_unbound");
        _store.Quarantine(parentJournalId, facts.ReasonCode);
    }
    public void ReconcileRecordedCommit()
    {
        var state = _store.Read(parentJournalId); var terminal = AttemptResultCodec.Read(state.Latest.Root, state.Attempt)
            ?? throw new IOException("attempt_recorded_commit_unverified");
        _store.Commit(parentJournalId, terminal.Facts.Lifecycle, terminal.Facts.ReasonCode);
    }

    private AttemptWriterObservation Observe()
    {
        var actual = writers();
        return actual.InstanceId == instance.InstanceId && actual.JournalId == parentJournalId ? actual :
            new(instance.InstanceId, parentJournalId, false, WriterState.Unknown);
    }
    private bool Collected(AttemptWriterState state) => NecessaryCollected(state) && AndroidCollected(state) &&
        PreviewDomainCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256).All(value => value.Closed) &&
        ExecutionCaptureCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256).All(value => value.Result is not null) &&
        ProviderLauncherCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .All(item => item.Stage is "stopped" or "retained" or "recoveredStopped" or "recoveredRetained") &&
        TargetJobStore.ReadJobs(state).All(item => item.Stage is "stopped" or "retained" or "recoveredStopped" or "recoveredRetained");

    private bool AndroidCollected(AttemptWriterState state)
    {
        var evidence = new AndroidControlEvidence(instance, database, contracts);
        foreach (var step in state.Total.Steps.Where(step => step.Target?.Kind == "android"))
            try { evidence.RequireStopped(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256, step.StepId); }
            catch (IOException error) when (error.Message is "execution_target_collection_pending" or "execution_android_writer_stop_unconfirmed")
            { return false; }
        return true;
    }

    private static bool NecessaryCollected(AttemptWriterState state)
    {
        var results = StepResultCodec.Read(state.Latest.Root, state.Total);
        return state.Jobs.All(item => item.ClosedElapsedMilliseconds.HasValue) && results.Length == state.Steps.Length &&
            StepResultCodec.Coverage(state.Latest.Root, state.Total).All(item =>
                item.Kind is StepCompletionKind.Collected or StepCompletionKind.NotDispatched);
    }
    private bool Restored(AttemptWriterState state, bool completeGroups)
    {
        try
        {
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration);
            using var transaction = connection.BeginTransaction(deferred: true);
            AttemptTerminalEvidence.RequireRestoredInside(connection, transaction, state, completeGroups); transaction.Commit(); return true;
        }
        catch (IOException) { return false; }
    }
    private static AttemptFacts ActiveFacts(AttemptWriterState state)
    {
        var steps = StepResultCodec.Coverage(state.Latest.Root, state.Total).Select(item =>
            item.Kind == StepCompletionKind.Collected ? ExecutionBindingRoles.Combine(state.Latest.Root, state.Total, item, state.Attempt.ExecutionPlan.Sha256) : item.Result is { } result ?
            new ExecutionStepFact(item.StepId, result.Latest.Engine == EngineOutcome.Running ? EngineOutcome.Unknown : result.Latest.Engine,
                result.Latest.Business, result.Latest.ReasonCode) : new ExecutionStepFact(item.StepId,
                    item.Kind == StepCompletionKind.NotDispatched ? EngineOutcome.NotStarted : EngineOutcome.Unknown,
                    BusinessOutcome.Unverified, item.NotDispatchedReason ?? "attempt_workers_uncollected")).ToArray();
        var facts = AttemptFactsPolicy.Aggregate(Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId), checked((int)state.Attempt.AttemptOrdinal),
            steps, LifecycleOutcome.Interrupted, RecoveryOutcome.Restored, "host_interrupted");
        var reason = state.Latest.Phase == JournalPhase.Quarantined ? state.Latest.Root.GetProperty("receipts").EnumerateArray().Last().GetProperty("detailCode").GetString()! : facts.ReasonCode;
        return facts with { Lifecycle = LifecycleOutcome.Running, Recovery = state.Latest.Phase == JournalPhase.Quarantined ? RecoveryOutcome.Quarantined : RecoveryOutcome.Pending,
            ReasonCode = reason };
    }
}
