using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class BoundPreparedAttempt(RunAttemptPlan plan, JournalDocument documentSource, bool isExecutionStart,
    ExecutionPlan total, AttemptValidatedRead validatedRead)
{
    internal RunAttemptPlan Plan { get; } = plan;
    internal bool IsExecutionStart { get; } = isExecutionStart;
    internal ExecutionPlan Total { get; } = total;
    internal AttemptValidatedRead ValidatedRead { get; } = validatedRead;
    // Read-only evidence consumers need the verified graph without allocating a mutable writer document.
    private readonly Lazy<JsonObject> _document = new(() => JsonNode.Parse(documentSource.Export())!.AsObject());
    internal JsonObject Document => _document.Value;
}

internal static class AttemptPreparedBinding
{
    internal static BlobReference Reference(RuntimeInstance instance, RegisteredJournal journal)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, journal.PlanPath));
        return new(journal.PlanPath, new FileInfo(path).Length, journal.PlanDigest);
    }

    internal static RunAttemptPlan ReadPlan(RuntimeInstance instance, ContractReader contracts, RegisteredJournal journal)
    {
        using var inputs = ReadPlanInputs(instance, contracts, journal);
        return inputs.Plan;
    }

    internal static AttemptBoundInputs ReadPlanInputs(RuntimeInstance instance, ContractReader contracts, RegisteredJournal journal)
    {
        if (journal.Kind != JournalKind.RunAttempt) throw new IOException("attempt_kind_unbound");
        var inputs = new AttemptPlanCodec(instance, contracts, new(instance, contracts)).ReadBound(Reference(instance, journal));
        try { RequireRegistration(journal, inputs.Plan); return inputs; }
        catch { inputs.Dispose(); throw; }
    }

    internal static AttemptMetadataPlan ReadPlanMetadata(RuntimeInstance instance, ContractReader contracts, RegisteredJournal journal)
    {
        var metadata = new AttemptPlanCodec(instance, contracts, new(instance, contracts)).ReadMetadata(Reference(instance, journal));
        RequireRegistration(journal, metadata.Plan);
        return metadata;
    }

    private static void RequireRegistration(RegisteredJournal journal, RunAttemptPlan plan)
    {
        if (journal.Kind != JournalKind.RunAttempt) throw new IOException("attempt_kind_unbound");
        if (plan.OperationId != journal.OperationId.ToString("D") || plan.JournalId != journal.JournalId.ToString("D") ||
            !plan.ResourceKeys.SequenceEqual(journal.ResourceKeys)) throw new IOException("attempt_plan_registration_unbound");
    }

    internal static BoundPreparedAttempt Read(RuntimeInstance instance, ContractReader contracts, StateDatabase database, Guid journalId,
        JournalStore? protectedHistory = null)
    {
        using var paths = OperatingSystem.IsWindows() ? DirectoryAttributeScope.Hold(instance) : null;
        using var blobLease = OperatingSystem.IsWindows() ? BlobReadScope.Hold(instance) : null;
        JournalReadScope.SelectParent(instance, contracts, journalId);
        if (protectedHistory is not null && (protectedHistory.Instance != instance || !ReferenceEquals(protectedHistory.Contracts, contracts)))
            throw new IOException("attempt_journal_owner_unbound");
        var snapshot = new RecoveryJournalRegistry(database).ReadSnapshot(journalId);
        RecoveryRegistryPolicy.Verify(snapshot);
        var registered = snapshot.Journals.Single(item => item.JournalId == journalId);
        if (registered.Kind != JournalKind.RunAttempt) throw new IOException("attempt_kind_unbound");
        using var inputs = new AttemptPlanCodec(instance, contracts, new(instance, contracts)).ReadBound(Reference(instance, registered));
        var plan = inputs.Plan; var total = inputs.Total;
        RequireRegistration(registered, plan);
        JournalResourceClaims.VerifyAttempt(registered, plan, total, JournalResourceClaims.Read(database, journalId));
        var journals = protectedHistory ?? new JournalStore(instance, contracts);
        using var historyScope = JournalReadScope.Hold(journals, journalId);
        var history = journals.ReadAllRevisions(journalId);
        using var parsedHistory = new AttemptReadScope(history, total);
        PreviewDomainCodec.RequireHistory(history, total, plan.ExecutionPlan.Sha256);
        var projected = history.SingleOrDefault(item => item.Revision == registered.Revision) ?? throw new IOException("attempt_sql_prefix_missing");
        if (projected.Phase != registered.Phase || snapshot.FileEffects.Any(item => item.JournalId == journalId)) throw new IOException("attempt_sql_projection_unbound");
        var gate = new JournalEffectGate(database).Read(journalId);
        var start = IsStart(database, registered.OperationId);
        if (gate.InstanceId != instance.InstanceId || gate.OperationId != registered.OperationId || gate.Kind != JournalKind.RunAttempt || gate.PlanDigest != registered.PlanDigest)
            throw new IOException("attempt_gate_binding_changed");
        var previousInvocations = start ? ExecutionInvocationHistory.Before(instance, database, contracts, plan, total) : System.Collections.Immutable.ImmutableDictionary<string, int>.Empty;
        var participantCount = 0; JournalDocument? preceding = null; var baselines = new HashSet<BlobReference>();
        var hookResults = new HashSet<HookResultIntent>();
        var stepResults = new HashSet<StepResultState>();
        var targets = new HistoricalTargetEvidence(instance, database, contracts, total, plan.ExecutionPlan.Sha256);
        foreach (var physical in history)
        {
            if (physical.Kind != JournalKind.RunAttempt || physical.Root.GetProperty("operationId").GetString() != plan.OperationId ||
                physical.Root.GetProperty("runId").GetString() != plan.RunId || physical.Root.GetProperty("attemptId").GetString() != plan.AttemptId ||
                physical.PlanDigest != registered.PlanDigest || physical.PlanPath != registered.PlanPath ||
                !physical.Root.GetProperty("resourceKeys").EnumerateArray().Select(item => item.GetString()!).SequenceEqual(plan.ResourceKeys))
                throw new IOException("attempt_journal_unbound");
            _ = ExecutionInvocationCodec.Read(physical.Root, total);
            _ = BindingTimeoutCodec.Read(physical.Root, total);
            foreach (var added in physical.Root.GetProperty("participants").EnumerateArray().Skip(participantCount).Where(BindingTimeoutCodec.IsParticipant))
                if (!start || physical.Phase != JournalPhase.Applying || gate.State != EffectGateState.Entered ||
                    added.GetProperty("identity").GetProperty("beforeRevision").GetInt64() != physical.Revision - 1) throw BindingTimeoutCodec.Invalid();
            foreach (var participant in physical.Root.GetProperty("participants").EnumerateArray().Skip(participantCount).Where(ExecutionInvocationCodec.IsParticipant))
            {
                if (!start || preceding is null) throw ExecutionInvocationCodec.Invalid();
                var beforeInvocation = new AttemptWriterState(registered, plan, total, preceding, gate,
                    WriterIntentCodec.Read(preceding.Root, total)) { IsExecutionStart = start, PreviousInvocations = previousInvocations };
                ExecutionInvocationStore.RequireStructure(instance, database, contracts, beforeInvocation, preceding, physical, ExecutionInvocationCodec.Decode(participant));
            }
            targets.Observe(physical.Root);
            var androidStartup = AndroidStartupCodec.Read(physical.Root, total, plan.ExecutionPlan.Sha256);
            var androidCollected = AndroidCollectedCodec.Read(physical.Root, total, plan.ExecutionPlan.Sha256);
            var androidRetention = AndroidRetentionCodec.Read(physical.Root, total, plan.ExecutionPlan.Sha256);
            AttemptWriterState.RequireOwnedJobs(physical.Root, total, plan.ExecutionPlan.Sha256);
            if ((TargetJobCodec.Read(physical.Root, total, plan.ExecutionPlan.Sha256).Length != 0 ||
                PcStageCodec.Read(physical.Root, total, plan.ExecutionPlan.Sha256).Length != 0 ||
                AndroidControlCodec.Read(physical.Root, total, plan.ExecutionPlan.Sha256).Length != 0 ||
                androidStartup.Length != 0 || androidCollected.Length != 0 || androidRetention.Length != 0) && !start)
                throw new IOException("execution_target_operation_unbound");
            var domains = PreviewDomainCodec.Read(physical.Root, total, plan.ExecutionPlan.Sha256);
            if (domains.Length != 0 && (!start || gate.State != EffectGateState.Entered)) throw PreviewDomainCodec.Invalid();
            var writers = WriterIntentCodec.ReadJobs(physical.Root, total);
            foreach (var writer in writers.Where(item => item.FileLogBaseline is not null && baselines.Add(item.FileLogBaseline!)))
            {
                var blobs = new ImmutableBlobStore(instance, contracts);
                var baseline = LogBaselineCodec.Read(blobs, writer.FileLogBaseline!, journalId, writer.StepId, writer.JobName,
                    total.Steps.Single(step => step.StepId == writer.StepId).LogTargets);
                foreach (var checkpoint in baseline.Candidates.Select(candidate => candidate.Checkpoint).OfType<BlobReference>().Distinct()) blobs.Verify(checkpoint);
            }
            var cancellation = DispatchIntentCodec.Read(physical.Root, total);
            var results = StepResultCodec.Read(physical.Root, total);
            var hooks = HookResultCodec.Read(physical.Root, total);
            hookResults.UnionWith(hooks);
            foreach (var participant in physical.Root.GetProperty("participants").EnumerateArray().Skip(participantCount)
                .Where(StepResultCodec.IsParticipant))
            {
                var intent = participant.GetProperty("identity").Deserialize<StepResultIntent>(StepResultCodec.Options)!;
                stepResults.Add(new(intent.Binding, intent, intent.Stage == "collected"));
            }
            var providers = ProviderTerminalCodec.Read(physical.Root, total);
            var launchers = ProviderLauncherCodec.ReadAll(physical.Root, total, plan.ExecutionPlan.Sha256);
            _ = BindingSuppressionCodec.Read(physical.Root, total);
            var retryHooks = RetryHookCodec.Read(physical.Root, total);
            if (retryHooks.Length != 0 && !start) throw RetryHookEvidence.Invalid();
            var skips = BindingSkipCodec.Read(physical.Root, total);
            if (skips.Length != 0 && !start) throw new IOException("execution_binding_skip_invalid");
            foreach (var added in physical.Root.GetProperty("participants").EnumerateArray().Skip(participantCount).Where(BindingSkipCodec.Matches))
            {
                if (preceding is null || physical.Phase is not (JournalPhase.Prepared or JournalPhase.Applying) ||
                    DispatchIntentCodec.Read(preceding.Root, total) is not null)
                    throw new IOException("execution_binding_skip_invalid");
                var skip = BindingSkipCodec.Decode(added, total);
                var priorSteps = BindingSkipCodec.Processed(preceding.Root, total, WriterIntentCodec.ReadJobs(preceding.Root, total));
                if (priorSteps.Length >= total.Steps.Count || total.Steps[priorSteps.Length].StepId != skip.StepId)
                    throw new IOException("execution_binding_skip_invalid");
            }
            var coverage = StepResultCodec.Coverage(physical.Root, total);
            var terminal = AttemptResultCodec.Read(physical.Root, plan);
            if (physical.Root.GetProperty("participants").EnumerateArray().Skip(participantCount).Any(PreviewDomainCodec.IsParticipant) &&
                physical.Phase is not (JournalPhase.Applying or JournalPhase.Quarantined)) throw PreviewDomainCodec.Invalid();
            if (physical.Root.GetProperty("participants").EnumerateArray().Skip(participantCount).Any(TargetJobCodec.IsParticipant) &&
                (gate.State != EffectGateState.Entered || physical.Phase is not (JournalPhase.Applying or JournalPhase.Quarantined)))
                throw new IOException("execution_target_stage_unbound");
            if (physical.Root.GetProperty("participants").EnumerateArray().Skip(participantCount).Any(PcStageCodec.IsParticipant) &&
                (gate.State != EffectGateState.Entered || physical.Phase != JournalPhase.Applying))
                throw new IOException("execution_pc_target_stage_unbound");
            if (physical.Root.GetProperty("participants").EnumerateArray().Skip(participantCount).Any(participant =>
                    AndroidControlCodec.IsParticipant(participant) || AndroidStartupCodec.IsParticipant(participant) ||
                    AndroidCollectedCodec.IsParticipant(participant) || AndroidRetentionCodec.IsParticipant(participant)) &&
                (gate.State != EffectGateState.Entered || physical.Phase != JournalPhase.Applying))
                throw new IOException("execution_android_stage_unbound");
            if (launchers.Length != 0 && (!start || gate.State != EffectGateState.Entered))
                throw new IOException("execution_provider_launcher_operation_unbound");
            if (providers.Length != 0 && !start) throw new IOException("execution_provider_operation_unbound");
            foreach (var added in providers.Where(item => item.BeforeRevision == physical.Revision - 1))
                if (physical.Phase != JournalPhase.Applying || gate.State != EffectGateState.Entered)
                    throw new IOException("execution_provider_publication_unbound");
            if ((results.Length != 0 || hooks.Length != 0 || terminal is not null) && !start) throw new IOException("execution_result_operation_unbound");
            if (cancellation is not null && !start) throw new IOException("execution_cancel_operation_unbound");
            if (writers.Length != 0 && !start) throw new IOException("execution_writer_operation_unbound");
            if (physical.Phase is not (JournalPhase.Prepared or JournalPhase.Applying or JournalPhase.Applied or JournalPhase.Committed or JournalPhase.Quarantined) ||
                physical.Phase is JournalPhase.Applying or JournalPhase.Applied && gate.State != EffectGateState.Entered)
                throw new IOException("attempt_nonprepared_recovery_unavailable");
            foreach (var participant in physical.Root.GetProperty("participants").EnumerateArray().Skip(participantCount)
                .Where(WriterIntentCodec.IsParticipant))
            {
                if (physical.Phase is not (JournalPhase.Prepared or JournalPhase.Applying)) throw new IOException("execution_writer_stage_unbound");
                if (participant.GetProperty("identity").GetProperty("stage").GetString() == "registered")
                {
                    if (preceding is null) throw new IOException("execution_writer_stage_unbound");
                    _ = ExecutionStepAdmission.RequireNext(preceding, total, participant.GetProperty("identity").GetProperty("stepId").GetString()!,
                        participant.GetProperty("identity").GetProperty("role").GetString()!);
                }
            }
            if (physical.Root.GetProperty("participants").EnumerateArray().Skip(participantCount).Any(DispatchIntentCodec.IsParticipant) &&
                physical.Phase is not (JournalPhase.Prepared or JournalPhase.Applying or JournalPhase.Applied)) throw new IOException("execution_cancel_stage_unbound");
            foreach (var participant in physical.Root.GetProperty("participants").EnumerateArray().Skip(participantCount).Where(item => StepResultCodec.IsParticipant(item) || HookResultCodec.IsParticipant(item) || BindingSuppressionCodec.IsParticipant(item)))
                if (physical.Phase != JournalPhase.Applying && !(physical.Phase == JournalPhase.Quarantined &&
                    (HookResultCodec.IsParticipant(participant) || BindingSuppressionCodec.IsParticipant(participant) || participant.GetProperty("identity").TryGetProperty("stage", out var stage) && stage.GetString() == "collected")))
                    throw new IOException("execution_step_result_stage_unbound");
            if (physical.Root.GetProperty("participants").EnumerateArray().Skip(participantCount).Any(AttemptResultCodec.IsParticipant) &&
                physical.Phase != JournalPhase.Committed) throw new IOException("execution_terminal_stage_unbound");
            if (physical.Root.GetProperty("participants").EnumerateArray().Skip(participantCount).Any(StepResultCodec.IsUndispatchedParticipant) &&
                (physical.Phase is not (JournalPhase.Prepared or JournalPhase.Applying or JournalPhase.Quarantined) || !start))
                throw new IOException("execution_undispatched_unbound");
            foreach (var participant in physical.Root.GetProperty("participants").EnumerateArray().Skip(participantCount)
                .Where(ProviderTerminalCodec.IsParticipant))
            {
                var added = providers.Single(item => item.Binding.StepId == participant.GetProperty("identity").GetProperty("binding").GetProperty("stepId").GetString());
                if (added.BeforeRevision != physical.Revision - 1) throw new IOException("execution_provider_publication_unbound");
                var replay = new ProviderControlReader(instance, database, contracts).Read(added.Binding, added.Artifact);
                if (replay.Terminal != added.Decision) throw new IOException("execution_provider_terminal_unbound");
            }
            foreach (var participant in physical.Root.GetProperty("participants").EnumerateArray().Skip(participantCount)
                .Where(ProviderLauncherCodec.IsParticipant))
                if (physical.Phase is not (JournalPhase.Applying or JournalPhase.Quarantined))
                    throw new IOException("execution_provider_launcher_stage_unbound");
            foreach (var added in physical.Root.GetProperty("participants").EnumerateArray().Skip(participantCount).Where(RetryHookCodec.IsParticipant))
            {
                var item = retryHooks.Single(item => item.StepId == added.GetProperty("identity").GetProperty("stepId").GetString() &&
                    item.Role == added.GetProperty("identity").GetProperty("role").GetString());
                if (preceding is null || item.BeforeRevision != preceding.Revision ||
                    item.Role == "preHook" && physical.Phase is not (JournalPhase.Prepared or JournalPhase.Applying) ||
                    item.Role == "postHook" && (physical.Phase != JournalPhase.Applying || gate.State != EffectGateState.Entered))
                    throw RetryHookEvidence.Invalid();
            }
            participantCount = physical.Root.GetProperty("participants").GetArrayLength();
            preceding = physical;
            if (gate.State != EffectGateState.Entered && writers.Any(writer => writer.ClosedElapsedMilliseconds is > 0))
                throw new IOException("execution_writer_elapsed_unbound");
            var completions = SnapshotIntentCodec.Read(physical.Root);
            VerifyReceipts(physical.Root.GetProperty("receipts"), completions, terminal);
            var noEffect = NoEffectReceipt.Recorded(physical.Root);
            if (completions.Count != 0 && (gate.State is not (EffectGateState.Sealed or EffectGateState.Entered) || noEffect))
                throw new IOException("configuration_snapshot_completion_parent_unbound");
            if (noEffect && gate.State != EffectGateState.Sealed || physical.Phase == JournalPhase.Committed && !noEffect && terminal is null ||
                terminal is not null && noEffect)
                throw new IOException("attempt_no_effect_decision_unbound");
            if (physical.Phase == JournalPhase.Applied || terminal?.Mode == "executed")
            {
                if (gate.State != EffectGateState.Entered || cancellation is null || coverage.Any(item =>
                    item.Kind is not (StepCompletionKind.Collected or StepCompletionKind.NotDispatched)))
                    throw new IOException("attempt_workers_uncollected");
            }
            if (terminal is not null) VerifyTerminal(plan, total, physical.Root, gate, cancellation, coverage, terminal);
        }
        targets.Verify();
        using var androidReadScope = new AndroidEvidenceScope(total, database);
        var androidReader = new AndroidControlEvidence(instance, database, contracts, androidReadScope);
        var captures = new CaptureEvidenceReader(instance, database, contracts).ReadMetadata(registered, plan, total, history, androidReader);
        if (captures.Length != 0 && (!start || gate.State != EffectGateState.Entered)) throw ExecutionCaptureCodec.Invalid();
        if (history[^1].Phase is JournalPhase.Applied or JournalPhase.Committed)
        {
            ExecutionCaptureCodec.RequireResolved(history[^1].Root, total, plan.ExecutionPlan.Sha256);
            PreviewDomainCodec.RequireResolved(history[^1].Root, total, plan.ExecutionPlan.Sha256);
        }
        VerifyHistoricalEvidence(instance, database, contracts, registered, plan, total, history[^1], hookResults, stepResults);
        // This owned total cannot escape until Read returns; every evidence access still reads its current file and SQL facts.
        _ = androidReader.ReadReferences(history[^1].Root, total, plan.ExecutionPlan.Sha256);
        _ = new ProviderLauncherEvidence(instance, database, contracts)
            .RequireAll(history[^1].Root, total, plan.ExecutionPlan.Sha256, verifyContents: true);
        ProviderLauncherRows.Verify(database, history[^1].Root, total, plan.ExecutionPlan.Sha256);
        var androidEvidence = new AndroidResultEvidence(instance, database, contracts, androidReader);
        foreach (var startup in AndroidStartupCodec.Read(history[^1].Root, total, plan.ExecutionPlan.Sha256))
            _ = androidEvidence.RequireStartup(history[^1].Root, total, plan.ExecutionPlan.Sha256, startup, verifyContent: true);
        foreach (var collected in AndroidCollectedCodec.Read(history[^1].Root, total, plan.ExecutionPlan.Sha256))
            _ = androidEvidence.RequireCollected(history[^1].Root, total, plan.ExecutionPlan.Sha256, collected, verifyContent: true);
        foreach (var step in total.Steps.Where(step => step.Hooks is { Count: > 0 } || step.Target?.Kind == "android"))
            _ = BindingOutputBudget.UsedFromValidatedRead(instance, database, contracts, registered, plan, total, history[^1], step.StepId, androidReader);
        _ = new RetryHookEvidence(instance, database, contracts).RequireAll(plan, total, history[^1].Root, verifyContents: true);
        if (start)
        {
            TargetRootFacts.Verify(instance, database, contracts, journalId, total, plan.ExecutionPlan.Sha256, history[^1].Root, gate.State);
            AttemptWriterState.VerifySql(database, registered, projected, total, plan.ExecutionPlan.Sha256, gate);
            _ = ExecutionCancellationRows.Verify(database, registered, plan, total, history[^1].Root, projected.Root);
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
            using var transaction = connection.BeginTransaction(deferred: true);
            ExecutionStepAdmission.RequireUndispatchedInside(connection, transaction,
                new(registered, plan, total, history[^1], gate, WriterIntentCodec.Read(history[^1].Root, total)) { IsExecutionStart = true }, history[^1]);
            transaction.Commit();
        }
        var receipt = new AttemptReceiptStore(database).Read(Guid.Parse(plan.AttemptId));
        var committed = history.SingleOrDefault(item => item.Phase == JournalPhase.Committed);
        if (receipt is not null && (committed is null || receipt != ExpectedReceipt(plan, committed)) ||
            registered.Phase == JournalPhase.Committed && receipt is null) throw new IOException("attempt_receipt_projection_unbound");
        return new(plan, history[^1], start, total,
            new AttemptValidatedRead(history, captures, plan.ExecutionPlan.Sha256, previousInvocations));
    }

    private static void VerifyHistoricalEvidence(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        RegisteredJournal registered, RunAttemptPlan plan, ExecutionPlan total, JournalDocument latest,
        IEnumerable<HookResultIntent> hooks, IEnumerable<StepResultState> results)
    {
        // Append-only history keeps these typed intents immutable; old observed results remain required after collection.
        var stepEvidence = new StepEvidenceReader(instance, database, contracts);
        var hookEvidence = new HookEvidenceReader(instance, database, contracts);
        foreach (var hook in hooks)
        {
            var context = stepEvidence.ContextFromValidatedRead(hook.Binding, registered, plan, total, latest, hook.Role);
            var evidence = hookEvidence.ReadFromValidatedContext(hook.Binding, hook.Role, hook.Artifact, context);
            if (evidence.Engine != hook.Engine || evidence.ReasonCode != hook.ReasonCode) throw new IOException("execution_hook_evidence_unbound");
        }
        foreach (var result in results)
        {
            var context = stepEvidence.ContextFromValidatedRead(result.Binding, registered, plan, total, latest);
            var evidence = stepEvidence.ReadFromValidatedContext(result.Binding, result.Latest.Artifact, result.IsCollected, context);
            if (evidence.Artifact != result.Latest.Artifact || evidence.Engine != result.Latest.Engine || evidence.Business != result.Latest.Business ||
                evidence.Generation != result.Latest.Generation || evidence.ReasonCode != result.Latest.ReasonCode)
                throw new IOException("execution_step_evidence_unbound");
        }
    }

    private static bool IsStart(StateDatabase database, Guid operationId)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT operation_kind FROM operations WHERE operation_id=$operation;";
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        return command.ExecuteScalar() as string == "execution.start";
    }

    internal static AttemptReceipt ExpectedReceipt(RunAttemptPlan plan, JournalDocument document) => new(document.JournalId,
        Guid.Parse(plan.OperationId), document.PlanDigest, Convert.ToHexStringLower(SHA256.HashData(document.Export())),
        AttemptResultCodec.Read(document.Root, plan)?.Facts ?? NoEffectReceipt.Facts(plan));

    private static void VerifyTerminal(RunAttemptPlan plan, ExecutionPlan total, JsonElement journal, EffectGateSnapshot gate, DispatchCloseIntent? close,
        IReadOnlyList<ExecutionStepCompletion> coverage, AttemptResultIntent terminal)
    {
        if (close is null || terminal.Mode != "executed" && (gate.State != EffectGateState.Sealed || close.ReasonCode != terminal.Facts.ReasonCode) ||
            terminal.Facts.Lifecycle == LifecycleOutcome.Completed && close.ReasonCode != DispatchIntentCodec.CompletionReason ||
            terminal.Facts.Lifecycle == LifecycleOutcome.Cancelled && close.ReasonCode != DispatchIntentCodec.Reason)
            throw new IOException("execution_terminal_dispatch_unbound");
        var steps = terminal.Mode != "executed" ? total.Steps.Select(step => new ExecutionStepFact(step.StepId,
            EngineOutcome.NotStarted, BusinessOutcome.Unverified, terminal.Facts.ReasonCode)).ToArray() :
            StepResultCodec.CompletedFacts(journal, total, plan.ExecutionPlan.Sha256).ToArray();
        if (terminal.Facts != AttemptFactsPolicy.Aggregate(Guid.Parse(plan.RunId), Guid.Parse(plan.AttemptId), checked((int)plan.AttemptOrdinal),
            steps, terminal.Facts.Lifecycle, terminal.Facts.Recovery, terminal.Facts.ReasonCode)) throw new IOException("execution_terminal_facts_unbound");
    }

    private static void VerifyReceipts(JsonElement receipts, IReadOnlyList<SnapshotCompletionIntent> completions, AttemptResultIntent? terminal)
    {
        foreach (var receipt in receipts.EnumerateArray())
        {
            var known = completions.Any(item => SnapshotIntentCodec.IsReceipt(receipt, item)) ||
                terminal is not null && AttemptResultCodec.IsReceipt(receipt, terminal) ||
                NoEffectReceipt.IsExact(receipt) || receipt.GetProperty("stepId").GetString() == "isolate-attempt" &&
                receipt.GetProperty("effect").GetString() == "unknown" && !receipt.TryGetProperty("outcomes", out _) &&
                receipt.GetProperty("detailCode").GetString() is "attempt_writer_stop_unconfirmed" or "attempt_effect_entry_observed" or "attempt_prepared_no_effect_unverified" or
                    "attempt_workers_uncollected" or "attempt_configuration_not_restored";
            if (!known) throw new JsonException("attempt_receipt_unrecognized");
        }
    }
}
