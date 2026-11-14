using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class BindingSkipStore(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease)
{
    [SupportedOSPlatform("windows")]
    internal bool TrySkip(Guid journalId, string stepId) => lease.PublishExecution(() =>
    {
        lease.VerifyHeld(instance);
        var state = AttemptWriterState.Read(instance, database, contracts, journalId);
        if (!state.IsExecutionStart || state.IsDispatchClosed || state.Latest.Revision != state.Journal.Revision ||
            state.Journal.Phase is not (JournalPhase.Prepared or JournalPhase.Applying)) throw Invalid();
        var processed = BindingSkipCodec.Processed(state.Latest.Root, state.Total, state.Jobs);
        var step = state.Total.Steps.Skip(processed.Length).FirstOrDefault();
        if (step?.StepId != stepId) throw Invalid();
        using var predecessors = ExecutionStepAdmission.HoldPredecessors(instance, database, contracts, state, processed.Length);
        var now = DateTimeOffset.UtcNow;
        var count = step.BindingId is null ? 0 : ExecutionBindingSuccesses.Today(database, now).GetValueOrDefault(Guid.Parse(step.BindingId));
        var reason = Reason(state, stepId, count);
        if (reason is null) return false;
        var day = ExecutionBindingSuccesses.Day(now);
        var decision = new ExecutionBindingSkip(BindingSkipCodec.Format, 1, stepId, reason, now, count)
            { DailyWindowStartUtc = day.Start, DailyWindowEndUtc = day.End };
        var document = JsonNode.Parse(state.Latest.Export())!.AsObject();
        document["revision"] = state.Latest.Revision + 1;
        document["updatedAt"] = now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        document["participants"]!.AsArray().Add(BindingSkipCodec.Participant(decision, step.ScriptId));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        _ = WriterIntentCodec.ReadJobs(JournalDocument.Read(bytes, contracts).Root, state.Total);
        new JournalStore(instance, contracts).Append(journalId, state.Latest.Revision, bytes);
        ExecutionAttemptProjection.Synchronize(instance, database, contracts,
            AttemptWriterState.Read(instance, database, contracts, journalId));
        return true;
    });

    internal static string? Reason(AttemptWriterState state, string stepId, long count)
    {
        var step = state.Total.Steps.Single(step => step.StepId == stepId);
        var facts = StepResultCodec.Coverage(state.Latest.Root, state.Total)
            .Where(item => item.Kind == StepCompletionKind.Collected).ToDictionary(item => item.StepId,
                item => ExecutionBindingRoles.Combine(state.Latest.Root, state.Total, item, state.Attempt.ExecutionPlan.Sha256));
        var dependencyFailed = step.DependsOn.Any(id =>
            !facts.TryGetValue(id, out var fact) || RetryDecision.ClassifyCollectedPrimary(fact) != AttemptOutcome.Succeeded);
        return dependencyFailed ? "execution_queue_dependency_not_successful" :
            step.MaximumSuccessfulRunsPerDay is > 0 && count >= step.MaximumSuccessfulRunsPerDay ? "execution_daily_success_limit" : null;
    }

    internal static void RequireNewInside(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument before, JournalDocument next)
    {
        var previous = BindingSkipCodec.Read(before.Root, state.Total).Select(item => item.StepId).ToHashSet(StringComparer.Ordinal);
        foreach (var item in BindingSkipCodec.Read(next.Root, state.Total).Where(item => !previous.Contains(item.StepId)))
        {
            if (!state.IsExecutionStart || next.Phase is not (JournalPhase.Prepared or JournalPhase.Applying) ||
                DispatchIntentCodec.Read(before.Root, state.Total) is not null) throw Invalid();
            var step = state.Total.Steps.Single(step => step.StepId == item.StepId);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM configuration.configuration_run_sessions WHERE parent_journal_id=$parent AND step_id=$step);";
            command.Parameters.AddWithValue("$parent", state.Journal.JournalId.ToString("D")); command.Parameters.AddWithValue("$step", item.StepId);
            if ((long)command.ExecuteScalar()! != 0) throw Invalid();
            if (item.ReasonCode == "execution_daily_success_limit")
            {
                command.CommandText = "SELECT count(*) FROM main.execution_binding_successes WHERE binding_id=$binding AND started_at_utc_ticks>=$start " +
                    "AND started_at_utc_ticks<$end AND started_at_utc_ticks<=$observed;";
                command.Parameters.AddWithValue("$binding", step.BindingId!); command.Parameters.AddWithValue("$start", item.DailyWindowStartUtc.UtcTicks);
                command.Parameters.AddWithValue("$end", item.DailyWindowEndUtc.UtcTicks); command.Parameters.AddWithValue("$observed", item.EvaluatedAtUtc.UtcTicks);
                if ((long)command.ExecuteScalar()! < item.SuccessfulRunsToday) throw Invalid();
            }
            if (Reason(state with { Latest = before }, item.StepId, item.SuccessfulRunsToday) != item.ReasonCode) throw Invalid();
        }
    }

    private static IOException Invalid() => new("execution_binding_skip_closed");
}
