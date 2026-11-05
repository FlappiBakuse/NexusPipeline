using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionInvocationHistory
{
    internal static ImmutableDictionary<string, int> Before(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, RunAttemptPlan attempt, ExecutionPlan total)
    {
        if (attempt.AttemptOrdinal == 1) return ImmutableDictionary<string, int>.Empty;
        var result = total.Steps.ToDictionary(step => step.StepId, _ => 0, StringComparer.Ordinal);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var source = StartSourceRows.Read(connection, transaction, Guid.Parse(attempt.OperationId));
        StartSourceRows.RequireAttempt(source, attempt);
        for (var ordinal = attempt.AttemptOrdinal - 1; ordinal >= 1; ordinal--)
        {
            if (source.RetrySourceOperationId is not { } operation) throw ExecutionInvocationCodec.Invalid();
            var previous = StartSourceRows.Read(connection, transaction, operation);
            var registered = RecoveryJournalRegistry.ReadInside(connection, transaction, previous.JournalId);
            var plan = AttemptPreparedBinding.ReadPlan(instance, contracts, registered);
            StartSourceRows.RequireAttempt(previous, plan);
            var retry = ExecutionRetryRows.After(connection, transaction, Guid.Parse(plan.AttemptId)) ?? throw ExecutionInvocationCodec.Invalid();
            var journal = new JournalStore(instance, contracts).Read(registered.JournalId);
            var receipt = AttemptReceiptStore.ReadInside(connection, transaction, Guid.Parse(plan.AttemptId)) ?? throw ExecutionInvocationCodec.Invalid();
            if (previous.AttemptOrdinal != ordinal || previous.RunId != source.RunId || previous.Identity.SubjectId != source.Identity.SubjectId ||
                registered.Phase != JournalPhase.Committed || registered.Revision != journal.Revision ||
                receipt != AttemptPreparedBinding.ExpectedReceipt(plan, journal) || retry.Frozen.NextOperationId != source.Identity.OperationId ||
                retry.PreviousPlan != AttemptPreparedBinding.Reference(instance, registered) ||
                retry.TotalPlan != AttemptPlanCodec.Reference(attempt.ExecutionPlan) ||
                AttemptPlanCodec.Reference(plan.ExecutionPlan) != AttemptPlanCodec.Reference(attempt.ExecutionPlan)) throw ExecutionInvocationCodec.Invalid();
            foreach (var pair in Count(journal.Root, total)) result[pair.Key] = checked(result[pair.Key] + pair.Value);
            source = previous;
        }
        if (source.RetrySourceOperationId is not null || result.Any(pair => pair.Value > (total.Steps.Single(step => step.StepId == pair.Key).MaximumAttempts ?? 1)))
            throw ExecutionInvocationCodec.Invalid();
        return result.ToImmutableDictionary(StringComparer.Ordinal);
    }

    internal static ImmutableDictionary<string, int> Count(JsonElement journal, ExecutionPlan total)
    {
        var markers = ExecutionInvocationCodec.Read(journal, total);
        var jobs = WriterIntentCodec.ReadJobs(journal, total);
        return total.Steps.ToImmutableDictionary(step => step.StepId,
            step => markers.Count(item => item.StepId == step.StepId) + (jobs.Any(job => job.StepId == step.StepId) ? 1 : 0), StringComparer.Ordinal);
    }

    internal static bool CanResume(ExecutionPlan total, JsonElement journal, IReadOnlyDictionary<string, int> previous)
    {
        var current = Count(journal, total);
        return total.Steps.All(step => current[step.StepId] + (previous.TryGetValue(step.StepId, out var count) ? count : 0) < (step.MaximumAttempts ?? 1));
    }
}
