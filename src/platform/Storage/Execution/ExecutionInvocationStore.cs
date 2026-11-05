using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class ExecutionInvocationStore(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease)
{
    internal static bool HasOrdinaryFailure(AttemptWriterState state, string stepId)
    {
        var result = StepResultCodec.Read(state.Latest.Root, state.Total).SingleOrDefault(item => item.Binding.StepId == stepId);
        var coverage = new ExecutionStepCompletion(stepId, StepCompletionKind.Collected, result, null);
        var fact = ExecutionBindingRoles.Combine(state.Latest.Root, state.Total, coverage, state.Attempt.ExecutionPlan.Sha256);
        return fact.ReasonCode is not (BindingTimeoutCodec.Reason or "execution_step_total_timeout") &&
            (fact.Engine != EngineOutcome.Unknown || fact.ReasonCode is "execution_business_failed" or "execution_step_stall_timeout") &&
            RetryDecision.ClassifyCollectedPrimary(fact) == AttemptOutcome.Failed;
    }

    internal static bool HasRemainingInvocation(AttemptWriterState state, string stepId) =>
        ExecutionInvocationCodec.Ordinal(state.Latest.Root, stepId) + state.PreviousInvocationCount(stepId) <
            (state.Total.Steps.Single(step => step.StepId == stepId).MaximumAttempts ?? 1);

    internal static bool CanRetry(AttemptWriterState state, string stepId) =>
        state.IsExecutionStart && DispatchIntentCodec.Read(state.Latest.Root, state.Total) is null && state.Latest.Phase == JournalPhase.Applying &&
        state.Gate.State == EffectGateState.Entered && HasRemainingInvocation(state, stepId) &&
        ExecutionBindingRoles.IsComplete(state.Latest.Root, state.Total, stepId) && HasOrdinaryFailure(state, stepId);

    [SupportedOSPlatform("windows")]
    internal bool Append(Guid journalId, string stepId, PreparedConfigurationRun? prepared, ExecutionTimeBudget budget) => lease.PublishExecution(() =>
    {
        lease.VerifyHeld(instance);
        if (budget.Read().Remaining <= TimeSpan.Zero) return false;
        var state = AttemptWriterState.Read(instance, database, contracts, journalId);
        if (state.Latest.Revision != state.Journal.Revision || !CanRetry(state, stepId)) throw ExecutionInvocationCodec.Invalid();
        AttemptTerminalEvidence.RequireArtifacts(instance, database, contracts, state);
        StepWritersEvidence.RequireStopped(instance, database, contracts, journalId, state.AllJobs.Where(job => job.StepId == stepId));
        using var restored = UnstartedConfigurationEvidence.Hold(instance, database, contracts, state, [stepId]);
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            AttemptTerminalEvidence.RequireRestoredInside(connection, transaction, state, stepId: stepId);
            transaction.Commit();
        }
        var plans = prepared?.Use(resources => resources.Select(item => item.Reference).ToImmutableArray()) ?? [];
        if (!TaskRetryEvidence.TryCapture(state, stepId, new(instance, contracts), out var taskRetry)) throw ExecutionInvocationCodec.Invalid();
        var item = new ExecutionInvocation(ExecutionInvocationCodec.Format, 1, stepId,
            ExecutionInvocationCodec.Ordinal(state.Latest.Root, stepId) + 1, state.Latest.Revision,
            state.Jobs.Last(job => job.StepId == stepId).JobName, DateTimeOffset.UtcNow, plans) { TaskRetry = taskRetry };
        var document = JsonNode.Parse(state.Latest.Export())!.AsObject();
        document["revision"] = state.Latest.Revision + 1;
        document["updatedAt"] = item.PreparedAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        document["participants"]!.AsArray().Add(ExecutionInvocationCodec.Participant(item, state.Total.Steps.Single(step => step.StepId == stepId).ScriptId));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        var next = JournalDocument.Read(bytes, contracts);
        RequireStructure(instance, database, contracts, state, state.Latest, next, item);
        if (budget.Read().Remaining <= TimeSpan.Zero) return false;
        new JournalStore(instance, contracts).Append(journalId, state.Latest.Revision, bytes);
        ExecutionAttemptProjection.Synchronize(instance, database, contracts,
            AttemptWriterState.Read(instance, database, contracts, journalId));
        return true;
    });

    internal static void RequireStructure(RuntimeInstance instance, StateDatabase database, ContractReader contracts, AttemptWriterState state,
        JournalDocument before, JournalDocument next, ExecutionInvocation item)
    {
        var prior = state with { Latest = before, Steps = WriterIntentCodec.Read(before.Root, state.Total) };
        if (!TaskRetryEvidence.TryCapture(prior, item.StepId, new(instance, contracts), out var taskRetry) ||
            !TaskRetryEvidence.Same(taskRetry, item.TaskRetry)) throw ExecutionInvocationCodec.Invalid();
        _ = ExecutionInvocationCodec.Read(next.Root, state.Total);
        var jobs = prior.Jobs;
        if (!CanRetry(prior, item.StepId) || before.Phase != next.Phase || next.Revision != before.Revision + 1 ||
            item.BeforeRevision != before.Revision || item.Ordinal != ExecutionInvocationCodec.Ordinal(before.Root, item.StepId) + 1 ||
            jobs.LastOrDefault()?.StepId != item.StepId || jobs.Last(job => job.StepId == item.StepId).JobName != item.PreviousJobName ||
            jobs.Where(job => job.StepId == item.StepId).Any(job => job.ClosedElapsedMilliseconds is null)) throw ExecutionInvocationCodec.Invalid();
        ProviderLauncherCodec.RequireResolved(before.Root, state.Total, state.Attempt.ExecutionPlan.Sha256, item.StepId);
        AndroidCollectedCodec.RequireResolved(before.Root, state.Total, state.Attempt.ExecutionPlan.Sha256, item.StepId);
        ExecutionCaptureCodec.RequireResolved(before.Root, state.Total, state.Attempt.ExecutionPlan.Sha256, item.StepId);
        PreviewDomainCodec.RequireResolved(before.Root, state.Total, state.Attempt.ExecutionPlan.Sha256, item.StepId);
        if (TargetJobCodec.Read(before.Root, state.Total, state.Attempt.ExecutionPlan.Sha256).Any(job => job.Binding.StepId == item.StepId &&
            job.Stage is not ("stopped" or "retained" or "recoveredStopped" or "recoveredRetained"))) throw ExecutionInvocationCodec.Invalid();
        var blobs = new ImmutableBlobStore(instance, contracts);
        var codec = new ConfigurationPlanCodec(instance, contracts, blobs);
        var states = new RunStateCodec(instance, contracts, blobs);
        var previous = ExecutionInvocationCodec.CurrentPlans(before.Root, state.Attempt, item.StepId)
            .Select(reference => codec.ReadStructure(reference)).ToDictionary(plan => plan.TargetPath, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inputs = new Dictionary<ConfigurationAddress, FrozenRunState>();
        foreach (var reference in item.ConfigurationPlans)
        {
            var child = codec.ReadStructure(reference);
            if (!seen.Add(child.TargetPath) || !previous.TryGetValue(child.TargetPath, out var old) || child.RunState is null || old.RunState is null ||
                child.JournalId == old.JournalId || child.OperationId == old.OperationId || child.Kind != JournalKind.ConfigExchange ||
                !FileTreeDelta.Compare(old.Before.Snapshot, child.Before.Snapshot).Changes.IsEmpty) throw ExecutionInvocationCodec.Invalid();
            var saved = states.ReadStructure(child.RunState);
            RunConfigurationBinding.Require(saved, item.StepId, child.TargetPath, state.Attempt, state.Total, blobs, contracts, laterInvocation: true, taskRetry: item.TaskRetry);
            if (item.TaskRetry is null) RunStateCodec.RequireRetrySource(saved, states.ReadStructure(old.RunState));
            else if (!inputs.TryAdd(saved.Source.Address, saved)) throw ExecutionInvocationCodec.Invalid();
        }
        if (!seen.SetEquals(previous.Keys)) throw ExecutionInvocationCodec.Invalid();
        if (inputs.Count != 0) PreparedRetryOrigin.RequireInvocationSource(instance, database, contracts, state.Attempt, state.Total, item, inputs);
    }

    internal static void RequireNewInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument before, JournalDocument next)
    {
        var old = ExecutionInvocationCodec.Read(before.Root, state.Total);
        foreach (var item in ExecutionInvocationCodec.Read(next.Root, state.Total).Skip(old.Length))
        {
            ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, state.Journal.JournalId);
            ExecutionStepAdmission.RequireJobsInside(connection, transaction, state.Journal.JournalId,
                WriterIntentCodec.ReadAllJobs(before.Root, state.Total));
            var prior = state with { Latest = before, Steps = WriterIntentCodec.Read(before.Root, state.Total) };
            AttemptTerminalEvidence.RequireRestoredInside(connection, transaction, prior, stepId: item.StepId);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            foreach (var job in prior.AllJobs.Where(job => job.StepId == item.StepId))
            {
                command.Parameters.Clear(); command.Parameters.AddWithValue("$parent", state.Journal.JournalId.ToString("D"));
                command.Parameters.AddWithValue("$job", job.JobName); command.Parameters.AddWithValue("$writer", job.WriterId.ToString("D"));
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM journal_writer_job_closures WHERE journal_id=$parent AND job_name=$job AND writer_id=$writer);";
                if ((long)command.ExecuteScalar()! != 1) throw ExecutionInvocationCodec.Invalid();
            }
        }
    }
}
