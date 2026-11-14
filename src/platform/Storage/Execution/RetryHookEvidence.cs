using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class RetryHookEvidence(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal InheritedHookSource? FindSuccessfulPre(AttemptWriterState state, string stepId)
    {
        var ordinal = ExecutionInvocationCodec.Ordinal(state.Latest.Root, stepId);
        if (ordinal > 1)
        {
            var local = ExecutionInvocationCodec.Views(state.Latest.Root).SelectMany(view => HookResultCodec.Read(view, state.Total))
                .LastOrDefault(item => item.StepId == stepId && item.Role == "preHook" && item.Engine == EngineOutcome.Succeeded);
            if (local is not null)
            {
                var sourceOrdinal = ExecutionInvocationCodec.Ordinal(ExecutionInvocationCodec.ForJob(state.Latest.Root, state.Total, local.Binding.JobName), stepId);
                if (sourceOrdinal < ordinal)
                {
                    var source = new InheritedHookSource(state.Journal.OperationId, state.Journal.JournalId,
                        AttemptPreparedBinding.Reference(instance, state.Journal), local) { InvocationOrdinal = sourceOrdinal };
                    _ = RequireSource(state.Attempt, state.Total, source, verifyContents: true); return source;
                }
            }
        }
        if (state.Attempt.AttemptOrdinal == 1) return null;
        foreach (var ancestor in Ancestors(state.Attempt))
        {
            var journal = new JournalStore(instance, contracts).Read(ancestor.JournalId);
            var result = HookResultCodec.ReadHistory(journal.Root, state.Total).FirstOrDefault(item =>
                item.StepId == stepId && item.Role == "preHook" && item.Engine == EngineOutcome.Succeeded);
            if (result is null) continue;
            var source = new InheritedHookSource(ancestor.OperationId, ancestor.JournalId, ancestor.Attempt, result);
            _ = RequireSource(state.Attempt, state.Total, source, verifyContents: true);
            return source;
        }
        return null;
    }

    internal IReadOnlyList<BlobReference> RequireAll(RunAttemptPlan attempt, ExecutionPlan total, JsonElement journal, bool verifyContents) =>
        ExecutionInvocationCodec.Views(journal).Select(ExecutionInvocationCodec.Current)
            .SelectMany(view => RequireCurrent(attempt, total, view, verifyContents)).Distinct().ToArray();

    private IReadOnlyList<BlobReference> RequireCurrent(RunAttemptPlan attempt, ExecutionPlan total, JsonElement journal, bool verifyContents)
    {
        var references = new List<BlobReference>();
        var jobs = WriterIntentCodec.ReadJobs(journal, total);
        foreach (var item in RetryHookCodec.Read(journal, total))
        {
            if (item.AttemptId.ToString("D") != attempt.AttemptId || item.ParentJournalId.ToString("D") != attempt.JournalId ||
                jobs.Any(job => job.StepId == item.StepId && job.Role == item.Role)) throw Invalid();
            var history = new JournalStore(instance, contracts).ReadAllRevisions(item.ParentJournalId);
            var before = history.SingleOrDefault(document => document.Revision == item.BeforeRevision) ?? throw Invalid();
            var added = history.SingleOrDefault(document => document.Revision == item.BeforeRevision + 1);
            if (added is null || !RetryHookCodec.Read(added.Root, total).Contains(item) ||
                RetryHookCodec.Read(before.Root, total).Any(prior => prior.StepId == item.StepId && prior.Role == item.Role))
                throw Invalid();
            if (item.Role == "preHook")
            {
                if (before.Phase is not (JournalPhase.Prepared or JournalPhase.Applying) || jobs.Any(job => job.StepId == item.StepId && job.Role == "preHook"))
                    throw Invalid();
                references.AddRange(RequireSource(attempt, total, item.Source!, verifyContents));
            }
            else
            {
                RequirePost(attempt, total, before, item, ExecutionInvocationHistory.Before(instance, database, contracts, attempt, total).GetValueOrDefault(item.StepId));
                references.Add(item.MainArtifact!);
            }
        }
        return references.Distinct().ToArray();
    }

    internal void RequireNew(AttemptWriterState state, RetryHookIntent item)
    {
        if (state.Latest.Revision != state.Journal.Revision || item.BeforeRevision != state.Latest.Revision ||
            item.ParentJournalId != state.Journal.JournalId || item.RunId.ToString("D") != state.Attempt.RunId ||
            item.AttemptId.ToString("D") != state.Attempt.AttemptId || state.Cancellation is not null ||
            DispatchIntentCodec.Read(state.Latest.Root, state.Total) is not null ||
            state.Jobs.Any(job => job.StepId == item.StepId && job.Role == item.Role)) throw Invalid();
        if (item.Role == "preHook")
        {
            if (state.Journal.Phase is not (JournalPhase.Prepared or JournalPhase.Applying) ||
                state.Gate.State != (state.Journal.Phase == JournalPhase.Prepared ? EffectGateState.Unentered : EffectGateState.Entered) ||
                state.Jobs.Any(job => job.StepId == item.StepId)) throw Invalid();
            _ = RequireSource(state.Attempt, state.Total, item.Source!, verifyContents: true);
        }
        else RequirePost(state.Attempt, state.Total, state.Latest, item, state.PreviousInvocationCount(item.StepId));
    }

    internal static void RequirePost(RunAttemptPlan attempt, ExecutionPlan total, JournalDocument before, RetryHookIntent item, int previousInvocations = 0)
    {
        var evaluation = item.Evaluation ?? throw Invalid();
        if (evaluation.InvocationOrdinal is { } invocation)
        {
            var resultLocal = StepResultCodec.Read(before.Root, total).SingleOrDefault(result => result.Binding.StepId == item.StepId);
            var main = ExecutionBindingRoles.Combine(before.Root, total, new(item.StepId, StepCompletionKind.Collected, resultLocal, null), attempt.ExecutionPlan.Sha256);
            var expectedRestored = total.ConfigurationSlots.All(slot => slot.StepId != item.StepId);
            var expectedDecision = new PostDeferralDecision(true, !expectedRestored, "execution_invocation_post_deferred");
            var limit = total.Steps.Single(step => step.StepId == item.StepId).TotalTimeoutSeconds;
            if (invocation != ExecutionInvocationCodec.Ordinal(before.Root, item.StepId) ||
                invocation + previousInvocations >= (total.Steps.Single(step => step.StepId == item.StepId).MaximumAttempts ?? 1) ||
                before.Phase != JournalPhase.Applying || resultLocal is not { IsCollected: true } || resultLocal.Latest.Artifact != item.MainArtifact ||
                DispatchIntentCodec.Read(before.Root, total) is not null || main.Engine == EngineOutcome.Unknown ||
                RetryDecision.ClassifyCollectedPrimary(main) != AttemptOutcome.Failed || evaluation.Main != main ||
                evaluation.Evidence != new RetryEvidence(AttemptOutcome.Failed, WriterState.Stopped, expectedRestored, false, true,
                    checked(invocation + previousInvocations), evaluation.ObservedAtUtc) || evaluation.Decision != expectedDecision ||
                evaluation.ObservedAtUtc.Offset != TimeSpan.Zero ||
                evaluation.ObservedAtUtc < DateTimeOffset.Parse(total.CreatedAt, System.Globalization.CultureInfo.InvariantCulture) ||
                evaluation.BindingConsumedMilliseconds is null or < 0 || limit is > 0 && evaluation.BindingConsumedMilliseconds >= limit * 1000 ||
                main.ReasonCode == BindingTimeoutCodec.Reason ||
                evaluation.Budget.Remaining <= TimeSpan.Zero || evaluation.Budget.ExhaustionReason is not null || evaluation.Budget.ConsumedMilliseconds < 0 ||
                (total.Budget.TotalTimeoutSeconds == 0 ? evaluation.Budget.Remaining != TimeSpan.MaxValue :
                    evaluation.Budget.Remaining > TimeSpan.FromSeconds(total.Budget.TotalTimeoutSeconds) ||
                    evaluation.Budget.ConsumedMilliseconds != (TimeSpan.FromSeconds(total.Budget.TotalTimeoutSeconds).Ticks -
                        evaluation.Budget.Remaining.Ticks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond) ||
                WriterIntentCodec.ReadJobs(before.Root, total).Any(job => job.StepId == item.StepId &&
                    (job.ClosedElapsedMilliseconds is null || job.Role == "postHook"))) throw Invalid();
            return;
        }
        var result = StepResultCodec.Read(before.Root, total).SingleOrDefault(result => result.Binding.StepId == item.StepId);
        var jobs = WriterIntentCodec.ReadJobs(before.Root, total).Where(job => job.StepId == item.StepId).ToArray();
        if (before.Phase != JournalPhase.Applying || total.Steps.Count != 1 || result is not { IsCollected: true } ||
            result.Latest.Engine == EngineOutcome.NotStarted || result.Latest.Artifact != item.MainArtifact ||
            jobs.Any(job => job.ClosedElapsedMilliseconds is null || job.Role == "postHook") ||
            DispatchIntentCodec.Read(before.Root, total) is not null) throw Invalid();
        var actual = ExecutionBindingRoles.Combine(before.Root, total, new(item.StepId, StepCompletionKind.Collected, result, null),
            attempt.ExecutionPlan.Sha256);
        var evidence = evaluation.Evidence;
        if (evaluation.Main != actual || evidence.PreviousOutcome != RetryDecision.ClassifyCollectedPrimary(actual) ||
            evidence.Writers != WriterState.Stopped || evidence.IsConfigurationRestored != (attempt.ConfigurationPlans.Count == 0) ||
            evidence.HasQuarantinedResources || !evidence.AreFrozenIdentitiesValid || evidence.CompletedAttempts != attempt.AttemptOrdinal ||
            evidence.PreviousEndedAt != evaluation.ObservedAtUtc || evaluation.ObservedAtUtc.Offset != TimeSpan.Zero ||
            evaluation.ObservedAtUtc < DateTimeOffset.Parse(total.CreatedAt, System.Globalization.CultureInfo.InvariantCulture) ||
            evaluation.Budget.ConsumedMilliseconds < 0 || evaluation.Budget.Remaining <= TimeSpan.Zero ||
            (total.Budget.TotalTimeoutSeconds == 0 ? evaluation.Budget.Remaining != TimeSpan.MaxValue :
                evaluation.Budget.Remaining > TimeSpan.FromSeconds(total.Budget.TotalTimeoutSeconds) ||
                evaluation.Budget.ConsumedMilliseconds != (TimeSpan.FromSeconds(total.Budget.TotalTimeoutSeconds).Ticks -
                    evaluation.Budget.Remaining.Ticks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond) ||
            evaluation.Decision != RetryDecision.EvaluatePostDeferral(RetryIntentCodec.Policy(total), evidence,
                evaluation.Budget, evaluation.ObservedAtUtc) || !evaluation.Decision.ShouldDefer) throw Invalid();
    }

    private IReadOnlyList<BlobReference> RequireSource(RunAttemptPlan attempt, ExecutionPlan total,
        InheritedHookSource source, bool verifyContents)
    {
        if (source.InvocationOrdinal is { } invocation)
        {
            var journal = new RecoveryJournalRegistry(database).ReadJournal(Guid.ParseExact(attempt.JournalId, "D"));
            var localPhysical = new JournalStore(instance, contracts).Read(journal.JournalId);
            var view = ExecutionInvocationCodec.ForJob(localPhysical.Root, total, source.Result.Binding.JobName);
            if (source.OperationId.ToString("D") != attempt.OperationId || source.JournalId != journal.JournalId ||
                source.Attempt != AttemptPreparedBinding.Reference(instance, journal) || source.Result.Binding.AttemptId.ToString("D") != attempt.AttemptId ||
                invocation != ExecutionInvocationCodec.Ordinal(view, source.Result.StepId) ||
                invocation >= ExecutionInvocationCodec.Ordinal(localPhysical.Root, source.Result.StepId) ||
                !HookResultCodec.Read(view, total).Contains(source.Result)) throw Invalid();
            var readerLocal = new HookEvidenceReader(instance, database, contracts);
            var valueLocal = readerLocal.Read(source.Result.Binding, "preHook", source.Result.Artifact, verifyContents);
            if (valueLocal.Engine != EngineOutcome.Succeeded || valueLocal.Root is null || valueLocal.ExitCode != 0) throw Invalid();
            return readerLocal.ReadReferences(source.Result.Binding, "preHook", source.Result.Artifact).Append(source.Attempt).Distinct().ToArray();
        }
        var ancestor = Ancestors(attempt).SingleOrDefault(item => item.OperationId == source.OperationId) ?? throw Invalid();
        if (ancestor.JournalId != source.JournalId || ancestor.Attempt != source.Attempt) throw Invalid();
        var parent = ReadAttempt(source.Attempt);
        var physical = new JournalStore(instance, contracts).Read(source.JournalId);
        if (parent.RunId != attempt.RunId || parent.AttemptOrdinal >= attempt.AttemptOrdinal ||
            AttemptPlanCodec.Reference(parent.ExecutionPlan) != AttemptPlanCodec.Reference(attempt.ExecutionPlan) ||
            physical.Phase != JournalPhase.Committed ||
            !HookResultCodec.ReadHistory(physical.Root, total).Contains(source.Result)) throw Invalid();
        var reader = new HookEvidenceReader(instance, database, contracts);
        var value = reader.Read(source.Result.Binding, "preHook", source.Result.Artifact, verifyContents);
        if (value.Engine != EngineOutcome.Succeeded || value.Root is null || value.ExitCode != 0) throw Invalid();
        return reader.ReadReferences(source.Result.Binding, "preHook", source.Result.Artifact).Append(source.Attempt).Distinct().ToArray();
    }

    private sealed record Ancestor(Guid OperationId, Guid JournalId, BlobReference Attempt);

    private IReadOnlyList<Ancestor> Ancestors(RunAttemptPlan attempt)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records);
        using var transaction = connection.BeginTransaction(deferred: true);
        var source = StartSourceRows.Read(connection, transaction, Guid.Parse(attempt.OperationId));
        StartSourceRows.RequireAttempt(source, attempt);
        var result = new List<Ancestor>();
        for (var expected = checked((int)attempt.AttemptOrdinal) - 1; expected >= 1; expected--)
        {
            if (source.RetrySourceOperationId is not { } previous) throw Invalid();
            var prior = StartSourceRows.Read(connection, transaction, previous);
            if (prior.AttemptOrdinal != expected || prior.RunId != source.RunId || prior.Identity.SubjectId != source.Identity.SubjectId)
                throw Invalid();
            var journal = RecoveryJournalRegistry.ReadInside(connection, transaction, prior.JournalId);
            var reference = AttemptPreparedBinding.Reference(instance, journal);
            var plan = ReadAttempt(reference);
            StartSourceRows.RequireAttempt(prior, plan);
            var retry = ExecutionRetryRows.After(connection, transaction, Guid.Parse(plan.AttemptId)) ?? throw Invalid();
            if (retry.Frozen.NextOperationId != source.Identity.OperationId ||
                retry.TotalPlan != AttemptPlanCodec.Reference(attempt.ExecutionPlan) || retry.PreviousPlan != reference ||
                journal.Phase != JournalPhase.Committed) throw Invalid();
            var physical = new JournalStore(instance, contracts).Read(journal.JournalId);
            var receipt = AttemptReceiptStore.ReadInside(connection, transaction, Guid.Parse(plan.AttemptId)) ?? throw Invalid();
            if (physical.Revision != journal.Revision || receipt != AttemptPreparedBinding.ExpectedReceipt(plan, physical)) throw Invalid();
            result.Add(new(prior.Identity.OperationId, prior.JournalId, AttemptPreparedBinding.Reference(instance, journal)));
            source = prior;
        }
        return result.AsReadOnly();
    }

    private RunAttemptPlan ReadAttempt(BlobReference reference)
    {
        StepEvidenceCodec.Plan(reference);
        using var input = new ImmutableBlobStore(instance, contracts).OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        return contracts.Read<RunAttemptPlan>(bytes, "run-attempt-plan.schema.json");
    }

    internal static ImmutableArray<RetryHookFact> Facts(JsonElement journal, ExecutionPlan total, string stepId)
    {
        var actual = HookResultCodec.Read(journal, total).Where(item => item.StepId == stepId)
            .Select(item => new RetryHookFact(item.Role == "preHook" ? ExecutionHookRole.Pre : ExecutionHookRole.Post,
                RetryHookDisposition.Collected, item.Engine));
        var resolved = RetryHookCodec.Read(journal, total).Where(item => item.StepId == stepId)
            .Select(item => new RetryHookFact(item.Role == "preHook" ? ExecutionHookRole.Pre : ExecutionHookRole.Post,
                item.Role == "preHook" ? RetryHookDisposition.InheritedSuccess : RetryHookDisposition.Deferred,
                item.Role == "preHook" ? EngineOutcome.Succeeded : EngineOutcome.NotStarted));
        var facts = actual.Concat(resolved).ToList();
        var suffix = StepResultCodec.ReadUndispatched(journal, total);
        var suppressed = BindingSuppressionCodec.Read(journal, total).SingleOrDefault(item => item.StepId == stepId);
        var input = total.Steps.Single(step => step.StepId == stepId);
        foreach (var declaration in ExecutionRetryAuthority.Hooks(input))
        {
            var role = declaration.Role == ExecutionHookRole.Pre ? "preHook" : "postHook";
            if (facts.Any(fact => fact.Role == declaration.Role)) continue;
            if (suffix?.StepIds.Contains(stepId) == true || suppressed?.Roles.Contains(role) == true)
                facts.Add(new(declaration.Role, RetryHookDisposition.NotDispatched, EngineOutcome.NotStarted));
        }
        return facts.OrderBy(item => item.Role).ToImmutableArray();
    }

    internal static bool CanRetry(JsonElement journal, ExecutionPlan total) => total.Steps.All(step =>
        RetryHookPolicy.CanRetry(ExecutionRetryAuthority.Hooks(step), Facts(journal, total, step.StepId)));

    internal static IOException Invalid() => new("execution_retry_hook_evidence_unbound");
}
