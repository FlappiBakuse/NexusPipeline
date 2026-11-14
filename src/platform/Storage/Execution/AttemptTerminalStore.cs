using System.Runtime.Versioning;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class AttemptTerminalStore(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease)
{
    internal AttemptWriterState Read(Guid parentJournalId)
    {
        lease.VerifyHeld(instance);
        var state = AttemptWriterState.Read(instance, database, contracts, parentJournalId);
        if (!state.IsExecutionStart) throw new IOException("execution_result_operation_unbound");
        return state;
    }

    internal void RecordObservation(Guid parentJournalId, PreparedStepObservation proof)
    {
        lease.RequirePublicationEntry();
        proof.Use(proof.Binding, evidence => lease.PublishExecution(() =>
        { Record(Read(parentJournalId), proof.Binding, evidence, "observed"); return true; }));
    }

    internal void RecordCollected(Guid parentJournalId, PreparedStepResult proof)
    {
        lease.RequirePublicationEntry();
        proof.Use(proof.Binding, evidence => lease.PublishExecution(() =>
        { Record(Read(parentJournalId), proof.Binding, evidence, "collected"); return true; }));
    }

    private void Record(AttemptWriterState state, StepEvidenceBinding binding, ExecutionStepEvidence evidence, string stage)
    {
        var job = state.Steps.SingleOrDefault(item => item.StepId == binding.StepId) ?? throw new IOException("execution_step_evidence_unbound");
        if (StepResultCodec.Binding(state.Latest.Root, job) != binding) throw new IOException("execution_step_evidence_unbound");
        if (state.Journal.Phase != JournalPhase.Applying && !(stage == "collected" && state.Journal.Phase == JournalPhase.Quarantined) ||
            state.Gate.State != EffectGateState.Entered || state.Latest.Revision != state.Journal.Revision)
            throw new IOException("execution_step_result_closed");
        if (new StepEvidenceReader(instance, database, contracts).Read(binding, evidence.Artifact, stage == "collected") != evidence)
            throw new IOException("execution_step_evidence_unbound");
        var prior = StepResultCodec.Read(state.Latest.Root, state.Total).SingleOrDefault(item => item.Binding.StepId == binding.StepId);
        var intended = new StepResultIntent(StepResultCodec.Format, 1, binding, stage, evidence.Artifact,
            evidence.Engine, evidence.Business, evidence.Generation, evidence.ReasonCode);
        if (prior?.Latest == intended) return;
        if (stage == "collected" && StepResultAdmission.SuppressesLocalStop(state.Cancellation))
        {
            evidence = new StepEvidenceFactory(instance, database, contracts, lease).NormalizeUnpublishedAfterClose(binding, evidence, prior);
            intended = intended with { Artifact = evidence.Artifact, Business = evidence.Business, ReasonCode = evidence.ReasonCode };
            if (prior?.Latest == intended) return;
        }
        if (state.IsDispatchClosed && (stage == "observed" || state.Cancellation?.ReasonCode != DispatchIntentCodec.CompletionReason) &&
            evidence.Business != (prior?.Latest.Business ?? BusinessOutcome.Unverified))
            throw new IOException("execution_judgement_admission_closed");
        BindingOutputBudget.RequireReplacement(instance, database, contracts, state, binding, "process", evidence.Artifact);
        Append(state, state.Latest.Phase, document => document["participants"]!.AsArray().Add(
            StepResultCodec.Participant(intended, state.Total.Steps.Single(item => item.StepId == binding.StepId).ScriptId)));
        Synchronize(state.Journal.JournalId);
    }

    internal void FreezeDispatch(Guid parentJournalId, bool interrupted)
        => FreezeDispatch(parentJournalId, interrupted ? DispatchIntentCodec.InterruptionReason : DispatchIntentCodec.CompletionReason);

    internal void FreezeDispatch(Guid parentJournalId, string fixedReason)
    {
        new ExecutionCancellationStore(instance, database, contracts, lease).Close(parentJournalId, fixedReason);
    }

    internal void CloseUndispatched(Guid parentJournalId) => lease.PublishExecution(() => CloseUndispatchedCore(parentJournalId));

    private void CloseUndispatchedCore(Guid parentJournalId)
    {
        var state = Read(parentJournalId); var close = DispatchIntentCodec.Read(state.Latest.Root, state.Total);
        if (close is null || state.Cancellation is null || state.Latest.Revision != state.Journal.Revision)
            throw new IOException("execution_dispatch_open");
        if (StepResultCodec.ReadUndispatched(state.Latest.Root, state.Total) is not null) return;
        if (state.Gate.State == EffectGateState.Entered)
        {
            foreach (var dispatched in ExecutionBindingRoles.DispatchedSteps(state.Jobs))
                SuppressRolesCore(parentJournalId, dispatched);
            state = Read(parentJournalId);
        }
        var dispatchedCount = BindingSkipCodec.Processed(state.Latest.Root, state.Total, state.Jobs).Length;
        var suffix = state.Total.Steps.Skip(dispatchedCount).Select(step => step.StepId).ToImmutableArray();
        if (suffix.IsEmpty) return;
        if (state.Journal.Phase is not (JournalPhase.Prepared or JournalPhase.Applying or JournalPhase.Quarantined))
            throw new IOException("execution_undispatched_unbound");
        var item = new ExecutionUndispatchedSteps(StepResultCodec.UndispatchedFormat, 1, parentJournalId, state.Journal.OperationId,
            state.Journal.PlanDigest, close.CancellationOperationId, close.ReasonCode, suffix);
        Append(state, state.Latest.Phase, document => document["participants"]!.AsArray().Add(
            StepResultCodec.UndispatchedParticipant(item, state.Total.Steps[dispatchedCount].ScriptId)),
            preflight: next =>
            {
                using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
                using var transaction = connection.BeginTransaction(deferred: true);
                ExecutionStepAdmission.RequireUndispatchedInside(connection, transaction, state, next); transaction.Commit();
            });
        Synchronize(parentJournalId);
    }

    internal void EnsureApplying(Guid parentJournalId)
        => lease.PublishExecution(() => EnsureApplyingCore(parentJournalId));

    private void EnsureApplyingCore(Guid parentJournalId)
    {
        var state = Read(parentJournalId);
        if (state.Gate.State != EffectGateState.Entered) throw new IOException("attempt_effect_entry_unverified");
        if (state.Latest.Phase == JournalPhase.Prepared) Append(state, JournalPhase.Applying, _ => { });
        Synchronize(parentJournalId);
    }

    internal void MarkApplied(Guid parentJournalId)
        => lease.PublishExecution(() => MarkAppliedCore(parentJournalId));

    private void MarkAppliedCore(Guid parentJournalId)
    {
        var state = Read(parentJournalId);
        if (state.Latest.Phase == JournalPhase.Committed) throw new IOException("attempt_recorded_commit_requires_reconciliation");
        if (state.Latest.Phase == JournalPhase.Applied)
        {
            using var recovered = state.Journal.Phase == JournalPhase.Quarantined ? HoldRestored(state, requireCompletedGroups: false) : null;
            Synchronize(parentJournalId); return;
        }
        if (state.Latest.Phase == JournalPhase.Prepared) { EnsureApplyingCore(parentJournalId); state = Read(parentJournalId); }
        if (state.Latest.Phase is not (JournalPhase.Applying or JournalPhase.Quarantined)) throw new IOException("attempt_applied_facts_unverified");
        AttemptTerminalEvidence.RequireStopped(database, parentJournalId);
        AttemptTerminalEvidence.RequireArtifacts(instance, database, contracts, state);
        AttemptTerminalEvidence.RequireCollected(database, state);
        new TargetJobEvidence(instance, database, contracts).RequireResolved(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        using var restored = state.Latest.Phase == JournalPhase.Quarantined ? HoldRestored(state, requireCompletedGroups: false) : null;
        Append(state, JournalPhase.Applied, _ => { }, verifiedRecovery: state.Latest.Phase == JournalPhase.Quarantined);
        Synchronize(parentJournalId);
    }

    internal void ReconcileNonterminal(Guid parentJournalId)
        => lease.PublishExecution(() => ReconcileNonterminalCore(parentJournalId));

    private void ReconcileNonterminalCore(Guid parentJournalId)
    {
        var state = Read(parentJournalId);
        if (state.Latest.Phase == JournalPhase.Committed || state.Latest.Revision == state.Journal.Revision) return;
        if (state.Latest.Phase == JournalPhase.Applied)
        {
            AttemptTerminalEvidence.RequireStopped(database, parentJournalId);
            AttemptTerminalEvidence.RequireArtifacts(instance, database, contracts, state);
            AttemptTerminalEvidence.RequireCollected(database, state);
            new TargetJobEvidence(instance, database, contracts).RequireResolved(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        }
        using var restored = state.Journal.Phase == JournalPhase.Quarantined && state.Latest.Phase == JournalPhase.Applied
            ? HoldRestored(state, requireCompletedGroups: false) : null;
        Synchronize(parentJournalId);
    }

    internal AttemptReceipt CommitPreparedCancellation(Guid parentJournalId, PreparedBudgetTerminal? budget = null, Action? releaseOwnership = null)
        => PublishTerminal(() => CommitCancellationCore(parentJournalId, budget), releaseOwnership);

    private AttemptReceipt CommitCancellationCore(Guid parentJournalId, PreparedBudgetTerminal? budget)
    {
        var state = Read(parentJournalId);
        var recorded = AttemptResultCodec.Read(state.Latest.Root, state.Attempt);
        if (state.Journal.Phase == JournalPhase.Committed)
            return recorded?.Mode == "noEffectCancellation" ? Receipt(state) : throw new OperationConflictException();
        if (state.Cancellation?.ReasonCode != DispatchIntentCodec.Reason || state.Latest.Phase is not (JournalPhase.Prepared or JournalPhase.Committed) ||
            state.Gate.State == EffectGateState.Entered) throw new IOException("attempt_no_effect_gate_unverified");
        AttemptTerminalEvidence.RequireStopped(database, parentJournalId);
        RequireEmptyJobs(state);
        if (state.Latest.Phase == JournalPhase.Prepared) { CloseUndispatchedCore(parentJournalId); state = Read(parentJournalId); }
        if (state.Gate.State == EffectGateState.Unentered)
        {
            new JournalEffectGate(database).SealUnentered(state.Journal, state.Gate.Revision); state = Read(parentJournalId);
        }
        using var untouched = NoEffectLease.Acquire(instance, contracts, database, state.Attempt,
            AttemptPreparedBinding.Reference(instance, state.Journal));
        using var restored = HoldRestored(state, requireCompletedGroups: true, allowUnregistered: true);
        if (recorded is null)
        {
            var facts = AttemptFactsPolicy.Aggregate(Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId), checked((int)state.Attempt.AttemptOrdinal),
                state.Total.Steps.Select(item => new ExecutionStepFact(item.StepId, EngineOutcome.NotStarted, BusinessOutcome.Unverified,
                    DispatchIntentCodec.Reason)).ToArray(), LifecycleOutcome.Cancelled, RecoveryOutcome.NotRequired, DispatchIntentCodec.Reason);
            AppendResult(state, facts, "noEffectCancellation");
        }
        Synchronize(parentJournalId, verifiedNoEffects: true, budget: budget);
        return Receipt(Read(parentJournalId));
    }

    internal void PrepareCancellation(Guid parentJournalId)
        => lease.PublishExecution(() => PrepareCancellationCore(parentJournalId));

    private void PrepareCancellationCore(Guid parentJournalId)
    {
        var state = Read(parentJournalId);
        if (state.Cancellation?.ReasonCode != DispatchIntentCodec.Reason || state.Latest.Phase != JournalPhase.Prepared ||
            state.Gate.State == EffectGateState.Entered) throw new IOException("attempt_no_effect_gate_unverified");
        AttemptTerminalEvidence.RequireStopped(database, parentJournalId); RequireEmptyJobs(state);
        CloseUndispatchedCore(parentJournalId); state = Read(parentJournalId);
        if (state.Gate.State == EffectGateState.Unentered) new JournalEffectGate(database).SealUnentered(state.Journal, state.Gate.Revision);
    }

    internal void PrepareStopped(Guid parentJournalId, LifecycleOutcome terminal, string reasonCode)
    {
        RequirePreparedStop(terminal, reasonCode);
        if (Read(parentJournalId).Gate.State == EffectGateState.Entered) throw new IOException("attempt_no_effect_gate_unverified");
        FreezeDispatch(parentJournalId, reasonCode);
        lease.PublishExecution(() => PrepareStoppedCore(parentJournalId, terminal, reasonCode));
    }

    private void PrepareStoppedCore(Guid parentJournalId, LifecycleOutcome terminal, string reasonCode)
    {
        var state = Read(parentJournalId);
        if (state.Cancellation?.ReasonCode != reasonCode) throw new OperationConflictException();
        if (AttemptResultCodec.Read(state.Latest.Root, state.Attempt) is { } recorded)
        {
            if (recorded.Mode != "noEffectStop" || recorded.Facts.Lifecycle != terminal || recorded.Facts.ReasonCode != reasonCode)
                throw new OperationConflictException();
            return;
        }
        if (state.Latest.Phase != JournalPhase.Prepared || state.Gate.State == EffectGateState.Entered)
            throw new IOException("attempt_no_effect_gate_unverified");
        AttemptTerminalEvidence.RequireStopped(database, parentJournalId); RequireEmptyJobs(state);
        using var untouched = NoEffectLease.Acquire(instance, contracts, database, state.Attempt,
            AttemptPreparedBinding.Reference(instance, state.Journal), allowPreparedChildren: true);
        CloseUndispatchedCore(parentJournalId); state = Read(parentJournalId);
        if (state.Gate.State == EffectGateState.Unentered) new JournalEffectGate(database).SealUnentered(state.Journal, state.Gate.Revision);
    }

    internal AttemptReceipt CommitPreparedStop(Guid parentJournalId, LifecycleOutcome terminal, string reasonCode, PreparedBudgetTerminal? budget = null, Action? releaseOwnership = null)
    {
        PrepareStopped(parentJournalId, terminal, reasonCode);
        return PublishTerminal(() =>
        {
            var state = Read(parentJournalId); var recorded = AttemptResultCodec.Read(state.Latest.Root, state.Attempt);
            if (state.Journal.Phase == JournalPhase.Committed) return Receipt(state);
            AttemptTerminalEvidence.RequireStopped(database, parentJournalId); RequireEmptyJobs(state);
            using var untouched = NoEffectLease.Acquire(instance, contracts, database, state.Attempt, AttemptPreparedBinding.Reference(instance, state.Journal));
            using var restored = HoldRestored(state, requireCompletedGroups: true, allowUnregistered: true);
            var facts = AttemptFactsPolicy.Aggregate(Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId), checked((int)state.Attempt.AttemptOrdinal),
                state.Total.Steps.Select(step => new ExecutionStepFact(step.StepId, EngineOutcome.NotStarted, BusinessOutcome.Unverified, reasonCode)).ToArray(),
                terminal, RecoveryOutcome.NotRequired, reasonCode);
            if (recorded is null) AppendResult(state, facts, "noEffectStop");
            else if (recorded.Facts != facts) throw new OperationConflictException();
            Synchronize(parentJournalId, verifiedNoEffects: true, budget: budget); return Receipt(Read(parentJournalId));
        }, releaseOwnership);
    }

    private static void RequirePreparedStop(LifecycleOutcome terminal, string reasonCode)
    {
        if (!(terminal == LifecycleOutcome.Interrupted && reasonCode is (DispatchIntentCodec.InterruptionReason or DispatchIntentCodec.TargetReason) ||
            terminal == LifecycleOutcome.Blocked && reasonCode == DispatchIntentCodec.BudgetReason ||
            terminal == LifecycleOutcome.Skipped && reasonCode == DispatchIntentCodec.CompletionReason))
            throw new ArgumentException("execution_prepared_stop_unrecognized");
    }

    internal AttemptReceipt Commit(Guid parentJournalId, LifecycleOutcome terminal, string reasonCode, PreparedBudgetTerminal? budget = null, Action? releaseOwnership = null)
        => PublishTerminal(() => CommitCore(parentJournalId, terminal, reasonCode, budget), releaseOwnership);

    private AttemptReceipt PublishTerminal(Func<AttemptReceipt> commit, Action? releaseOwnership)
        => lease.PublishExecution(() =>
        {
            var receipt = commit();
            releaseOwnership?.Invoke(); return receipt;
        });

    private AttemptReceipt CommitCore(Guid parentJournalId, LifecycleOutcome terminal, string reasonCode, PreparedBudgetTerminal? budget)
    {
        var state = Read(parentJournalId); var recorded = AttemptResultCodec.Read(state.Latest.Root, state.Attempt);
        if (terminal is not (LifecycleOutcome.Completed or LifecycleOutcome.Cancelled or LifecycleOutcome.Interrupted or LifecycleOutcome.Skipped))
            throw new ArgumentException("execution_terminal_lifecycle_unrecognized");
        if (state.Cancellation is null || terminal == LifecycleOutcome.Completed && state.Cancellation.ReasonCode != DispatchIntentCodec.CompletionReason ||
            terminal == LifecycleOutcome.Cancelled && state.Cancellation.ReasonCode != DispatchIntentCodec.Reason)
            throw new OperationConflictException();
        if (terminal == LifecycleOutcome.Completed && recorded?.Facts.Lifecycle == LifecycleOutcome.Skipped) terminal = LifecycleOutcome.Skipped;
        if (recorded is not null && (recorded.Facts.Lifecycle != terminal || recorded.Facts.ReasonCode != reasonCode || recorded.Mode != "executed"))
            throw new OperationConflictException();
        if (state.Journal.Phase == JournalPhase.Committed) return Receipt(state);
        if (state.Journal.Phase != JournalPhase.Applied || state.Latest.Phase is not (JournalPhase.Applied or JournalPhase.Committed))
            throw new IOException("attempt_applied_facts_unverified");
        AttemptTerminalEvidence.RequireStopped(database, parentJournalId);
        AttemptTerminalEvidence.RequireArtifacts(instance, database, contracts, state);
        AttemptTerminalEvidence.RequireCollected(database, state);
        new TargetJobEvidence(instance, database, contracts).RequireResolved(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        using var restored = HoldRestored(state, requireCompletedGroups: true);
        var steps = AttemptTerminalEvidence.Steps(state);
        if (terminal == LifecycleOutcome.Completed && steps.All(step => step.Engine == EngineOutcome.NotStarted))
            terminal = LifecycleOutcome.Skipped;
        var facts = AttemptFactsPolicy.Aggregate(Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId), checked((int)state.Attempt.AttemptOrdinal),
            steps, terminal, RecoveryOutcome.Restored, reasonCode);
        if (recorded is null) AppendResult(state, facts, "executed");
        else if (recorded.Facts != facts) throw new OperationConflictException();
        Synchronize(parentJournalId, budget: budget); return Receipt(Read(parentJournalId));
    }

    internal void Quarantine(Guid parentJournalId, string reason)
        => lease.PublishExecution(() => QuarantineCore(parentJournalId, reason));

    private void QuarantineCore(Guid parentJournalId, string reason)
    {
        if (reason is not ("attempt_writer_stop_unconfirmed" or "attempt_workers_uncollected" or "attempt_configuration_not_restored"))
            throw new ArgumentException("attempt_recovery_reason_unrecognized");
        var state = Read(parentJournalId);
        if (state.Latest.Phase == JournalPhase.Quarantined) return;
        if (state.Latest.Root.GetProperty("receipts").EnumerateArray().Any(item => item.GetProperty("stepId").GetString() == "isolate-attempt"))
            throw new IOException("execution_recovery_capacity_reserved");
        Append(state, JournalPhase.Quarantined, document => document["receipts"]!.AsArray().Add(new JsonObject
        {
            ["receiptId"] = Guid.NewGuid().ToString("D"), ["recordedAt"] = document["updatedAt"]!.GetValue<string>(),
            ["stepId"] = "isolate-attempt", ["effect"] = "unknown", ["detailCode"] = reason
        }));
        Synchronize(parentJournalId);
    }

    private void RequireEmptyJobs(AttemptWriterState state)
    {
        foreach (var step in state.Jobs)
        {
            var closure = new JobClosureStore(instance, database, contracts).ReadForExecutionEvidence(state.Journal.JournalId, step.WriterId, step.JobName, step.SessionId)
                ?? throw new IOException("writer_job_closure_missing");
            if (step.ClosedElapsedMilliseconds != 0 || closure.WasExecuted || closure.RunElapsedMilliseconds != 0)
                throw new IOException("attempt_no_effect_writer_unverified");
        }
    }

    private IDisposable HoldRestored(AttemptWriterState state, bool requireCompletedGroups, bool allowUnregistered = false)
    {
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            AttemptTerminalEvidence.RequireRestoredInside(connection, transaction, state, requireCompletedGroups, allowUnregistered); transaction.Commit();
        }
        return RunBindingEvidence.HoldRestored(instance, database, contracts, state,
            state.Total.Steps.Select(step => step.StepId));
    }

    private void AppendResult(AttemptWriterState state, AttemptFacts facts, string mode)
    {
        var result = new AttemptResultIntent(AttemptResultCodec.Format, 1, Guid.NewGuid(), state.Journal.JournalId,
            state.Journal.OperationId, state.Journal.PlanDigest, mode, facts);
        Append(state, JournalPhase.Committed, document =>
        {
            document["participants"]!.AsArray().Add(AttemptResultCodec.Participant(result, state.Journal.ResourceKeys[0]));
            document["receipts"]!.AsArray().Add(AttemptResultCodec.Receipt(result, document["updatedAt"]!.GetValue<string>()));
        }, noEffects: mode != "executed");
    }

    private void Append(AttemptWriterState state, JournalPhase phase, Action<JsonObject> change, bool noEffects = false,
        bool verifiedRecovery = false, Action<JournalDocument>? preflight = null)
    {
        var document = JsonNode.Parse(state.Latest.Export())!.AsObject(); document["revision"] = state.Latest.Revision + 1;
        document["phase"] = phase.ToString().ToLowerInvariant(); document["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        change(document); var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        var parsed = JournalDocument.Read(bytes, contracts); _ = StepResultCodec.Read(parsed.Root, state.Total);
        _ = AttemptResultCodec.Read(parsed.Root, state.Attempt);
        _ = StepResultCodec.Coverage(parsed.Root, state.Total);
        _ = HookResultCodec.Read(parsed.Root, state.Total);
        _ = BindingSuppressionCodec.Read(parsed.Root, state.Total); preflight?.Invoke(parsed);
        new JournalStore(instance, contracts).Append(state.Journal.JournalId, state.Latest.Revision, bytes,
            noEffects ? new(true, false, false, false) : null, verifiedRecovery);
    }
    private void Synchronize(Guid parentJournalId, bool verifiedNoEffects = false, PreparedBudgetTerminal? budget = null) =>
        ExecutionAttemptProjection.Synchronize(instance, database, contracts, Read(parentJournalId), verifiedNoEffects, budget: budget);
    private AttemptReceipt Receipt(AttemptWriterState state) => new AttemptReceiptStore(database).Read(Guid.Parse(state.Attempt.AttemptId))
        ?? throw new IOException("attempt_receipt_projection_unbound");
    internal bool RecordRetryHook(Guid parentJournalId, PreparedHookDecision proof)
    {
        lease.RequirePublicationEntry();
        return proof.Use(parentJournalId, intended => lease.PublishExecution(() =>
        {
            var state = Read(parentJournalId);
            var existing = RetryHookCodec.Read(state.Latest.Root, state.Total)
                .SingleOrDefault(item => item.StepId == intended.StepId && item.Role == intended.Role);
            if (existing == intended) { Synchronize(parentJournalId); return true; }
            if (existing is not null) throw RetryHookEvidence.Invalid();
            if (state.Cancellation is not null || intended.Role == "postHook" && !proof.HasCurrentBudget) return false;
            proof.RequireNew(state);
            Append(state, state.Latest.Phase, document => document["participants"]!.AsArray().Add(
                RetryHookCodec.Participant(intended, state.Total.Steps.Single(step => step.StepId == intended.StepId).ScriptId)));
            Synchronize(parentJournalId); return true;
        }));
    }

    internal void RecordHookCollected(Guid parentJournalId, PreparedHookResult proof)
    {
        lease.RequirePublicationEntry();
        var binding = proof.Binding;
        proof.Use(binding, proof.Role, (artifact, evidence) => lease.PublishExecution(() =>
        {
            var current = Read(parentJournalId);
            var job = current.Jobs.SingleOrDefault(item => item.StepId == binding.StepId && item.Role == proof.Role)
                ?? throw new IOException("execution_hook_evidence_unbound");
            if (StepResultCodec.Binding(current.Latest.Root, job) != binding) throw new IOException("execution_hook_evidence_unbound");
            var intended = new HookResultIntent(HookResultCodec.Format, 1, binding.StepId, proof.Role,
                binding, artifact, evidence.Engine, evidence.ReasonCode);
            var prior = HookResultCodec.Read(current.Latest.Root, current.Total)
                .SingleOrDefault(item => item.StepId == binding.StepId && item.Role == proof.Role);
            if (prior == intended) return true;
            if (prior is not null || current.Journal.Phase is not (JournalPhase.Applying or JournalPhase.Quarantined) ||
                current.Latest.Revision != current.Journal.Revision || current.Gate.State != EffectGateState.Entered)
                throw new IOException("execution_hook_result_closed");
            if (new HookEvidenceReader(instance, database, contracts).Read(binding, proof.Role, artifact) != evidence)
                throw new IOException("execution_hook_evidence_changed");
            BindingOutputBudget.RequireReplacement(instance, database, contracts, current, binding, proof.Role, artifact);
            Append(current, current.Latest.Phase, document => document["participants"]!.AsArray().Add(
                HookResultCodec.Participant(intended, current.Total.Steps.Single(step => step.StepId == binding.StepId).ScriptId)));
            Synchronize(parentJournalId); return true;
        }));
    }

    internal void SuppressRemainingRoles(Guid parentJournalId, string stepId) =>
        lease.PublishExecution(() => SuppressRolesCore(parentJournalId, stepId));

    private void SuppressRolesCore(Guid parentJournalId, string stepId)
    {
        var state = Read(parentJournalId);
        var current = state.Jobs.Where(job => job.StepId == stepId).ToArray();
        if (current.Length == 0) throw new IOException("execution_binding_not_dispatched");
        if (BindingSuppressionCodec.Read(state.Latest.Root, state.Total).Any(item => item.StepId == stepId)) return;
        var step = state.Total.Steps.Single(step => step.StepId == stepId);
        var missing = ExecutionBindingRoles.Required(step).Where(role => current.All(job => job.Role != role) && !RetryHookCodec.ResolvedRoles(state.Latest.Root, state.Total, stepId).Contains(role)).ToImmutableArray();
        if (missing.IsEmpty) return;
        if (state.Gate.State != EffectGateState.Entered || state.Journal.Phase is not (JournalPhase.Applying or JournalPhase.Quarantined) ||
            state.Latest.Revision != state.Journal.Revision) throw new IOException("execution_binding_suppression_closed");
        StepWritersEvidence.RequireStopped(instance, database, contracts, parentJournalId, current);
        AttemptTerminalEvidence.RequireArtifacts(instance, database, contracts, state);
        var close = DispatchIntentCodec.Read(state.Latest.Root, state.Total);
        if (state.Cancellation is not null && close is null) throw new IOException("execution_cancel_projection_required");
        var targetStage = PcStageCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .SingleOrDefault(item => item.Binding.StepId == stepId);
        var localReason = BindingTimeoutCodec.Read(state.Latest.Root, state.Total).Any(timeout => timeout.StepId == stepId)
            ? BindingTimeoutCodec.Reason : targetStage?.Outcome is TargetExecutionOutcome.LaunchFailed or TargetExecutionOutcome.ReadinessTimedOut
            ? targetStage.ReasonCode : "execution_pre_hook_failed";
        var item = new ExecutionBindingSuppression(BindingSuppressionCodec.Format, 1, stepId, missing,
            close?.ReasonCode ?? localReason, close?.CancellationOperationId);
        Append(state, state.Latest.Phase, document => document["participants"]!.AsArray().Add(
            BindingSuppressionCodec.Participant(item, step.ScriptId)),
            preflight: next => _ = BindingSuppressionCodec.Read(next.Root, state.Total));
        Synchronize(parentJournalId);
    }

}
