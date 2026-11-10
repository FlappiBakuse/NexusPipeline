using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class StepWritersEvidence
{
    [SupportedOSPlatform("windows")]
    internal static void RequireConfigurationStopped(RuntimeInstance instance, StateDatabase database, ContractReader contracts, Guid childJournalId, AttemptWriterState? verifiedParent = null)
    {
        Guid parentId; string stepId;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT s.parent_journal_id,s.step_id,o.operation_kind FROM configuration_run_resources r " +
                "JOIN configuration_run_sessions s USING(session_id) JOIN operation_journals p ON p.journal_id=s.parent_journal_id " +
                "JOIN operations o ON o.operation_id=p.operation_id WHERE r.journal_id=$child;";
            command.Parameters.AddWithValue("$child", childJournalId.ToString("D"));
            using var row = command.ExecuteReader();
            if (!row.Read()) return;
            parentId = Guid.Parse(row.GetString(0)); stepId = row.GetString(1);
            if (row.GetString(2) != "execution.start") return;
            if (row.Read()) throw new IOException("execution_configuration_membership_unbound");
        }
        var registry = new RecoveryJournalRegistry(database).ReadSnapshot(parentId);
        var parent = registry.Journals.Single(item => item.JournalId == parentId);
        var expected = verifiedParent?.Journal;
        if (expected is not null && (expected.JournalId != parent.JournalId || expected.OperationId != parent.OperationId ||
            expected.Kind != parent.Kind || expected.Phase != parent.Phase || expected.Revision != parent.Revision ||
            expected.PlanPath != parent.PlanPath || expected.PlanDigest != parent.PlanDigest ||
            !expected.ResourceKeys.SequenceEqual(parent.ResourceKeys))) throw new IOException("execution_configuration_membership_unbound");
        var attempt = verifiedParent?.Attempt ?? AttemptPreparedBinding.ReadPlan(instance, contracts, parent);
        var child = registry.Journals.Single(item => item.JournalId == childJournalId);
        var total = verifiedParent?.Total ?? new ExecutionPlanCodec(instance, contracts, new(instance, contracts)).Read(AttemptPlanCodec.Reference(attempt.ExecutionPlan)).ReadWire();
        var document = verifiedParent?.Latest ?? new JournalStore(instance, contracts).Read(parentId);
        if (!ExecutionInvocationCodec.Plans(document.Root, total, attempt).Any(item => item.StepId == stepId && item.Plan.RelativePath == child.PlanPath && item.Plan.Sha256 == child.PlanDigest))
            throw new IOException("execution_configuration_membership_unbound");
        PreviewDomainEvidence.RequireStopped(database,
            verifiedParent ?? AttemptWriterState.Read(instance, database, contracts, parentId), stepId);
        new CaptureEvidenceReader(instance, database, contracts).RequireResolvedForRecovery(parent, attempt, total,
            new JournalStore(instance, contracts).ReadAllRevisions(parentId), stepId);
        new TargetJobEvidence(instance, database, contracts).RequireResolved(document.Root, total, attempt.ExecutionPlan.Sha256, stepId);
        new ProviderLauncherEvidence(instance, database, contracts).RequireResolved(document.Root, total, attempt.ExecutionPlan.Sha256, stepId);
        new AndroidControlEvidence(instance, database, contracts).RequireStopped(document.Root, total, attempt.ExecutionPlan.Sha256, stepId);
        var jobs = WriterIntentCodec.ReadJobs(document.Root, total);
        foreach (var primary in jobs.Where(item => item.StepId == stepId && item.Role == "process"))
        {
            var closure = new JobClosureStore(instance, database, contracts).ReadForExecutionEvidence(parentId,
                primary.WriterId, primary.JobName, primary.SessionId);
            if (closure?.WasExecuted == true) ExecutionStepAdmission.RequireRequiredRoles(total, jobs, stepId, "process", document.Root);
        }
        var input = total.Steps.Single(step => step.StepId == stepId);
        if (input.Hooks is { Count: > 0 })
        {
            var gate = new JournalEffectGate(database).Read(parentId);
            if (gate.State is not (EffectGateState.Entered or EffectGateState.Sealed))
                throw new IOException("execution_hook_dispatch_pending");
            var represented = GroupRoles(document.Root, total, stepId, jobs.Where(job => job.StepId == stepId));
            if (gate.State == EffectGateState.Entered && !represented.SetEquals(ExecutionBindingRoles.Required(input))) throw new IOException("execution_hook_dispatch_pending");
        }
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            ExecutionStepAdmission.RequireJobsInside(connection, transaction, parentId, WriterIntentCodec.ReadAllJobs(document.Root, total)); transaction.Commit();
        }
        RequireStopped(instance, database, contracts, parentId, jobs.Where(item => item.StepId == stepId));
    }

    [SupportedOSPlatform("windows")]
    internal static StepWriterState[] ReadStoppedGroup(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, Guid parentId, string stepId)
    {
        var state = AttemptWriterState.Read(instance, database, contracts, parentId);
        var jobs = state.Jobs.Where(job => job.StepId == stepId).ToArray();
        RequireStopped(instance, database, contracts, parentId, jobs);
        if (state.Gate.State == EffectGateState.Entered && !GroupRoles(state.Latest.Root, state.Total, stepId, jobs)
                .SetEquals(ExecutionBindingRoles.Required(state.Total.Steps.Single(step => step.StepId == stepId))))
            throw new IOException("configuration_group_dispatch_pending");
        return jobs;
    }

    private static HashSet<string> GroupRoles(JsonElement journal, ExecutionPlan total, string stepId, IEnumerable<StepWriterState> jobs)
    {
        var suppressed = BindingSuppressionCodec.Read(journal, total).SingleOrDefault(item => item.StepId == stepId);
        return jobs.Select(job => job.Role).Concat(suppressed?.Roles ?? [])
            .Concat(RetryHookCodec.ResolvedRoles(journal, total, stepId)).ToHashSet(StringComparer.Ordinal);
    }

    [SupportedOSPlatform("windows")]
    internal static void RequireStopped(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        Guid parentJournalId, IEnumerable<StepWriterState> jobs)
    {
        var closures = new JobClosureStore(instance, database, contracts);
        foreach (var job in jobs)
        {
            var closure = closures.ReadForExecutionEvidence(parentJournalId, job.WriterId, job.JobName, job.SessionId)
                ?? throw new IOException("writer_job_closure_missing");
            if (job.ClosedElapsedMilliseconds is null || closure.RunElapsedMilliseconds != job.ClosedElapsedMilliseconds ||
                WriterStopEvidence.ObserveJob(job.JobName, job.SessionId) == WriterState.Running ||
                closure.Family.Any(member => WriterStopEvidence.ObserveProcess(member) != WriterState.Stopped))
                throw new IOException("execution_step_writer_stop_unconfirmed");
            var creator = JournalWriterStore.CurrentWriter.Process;
            if (closure.Creator != creator && WriterStopEvidence.ObserveProcess(closure.Creator) != WriterState.Stopped)
                throw new IOException("execution_step_writer_owner_running");
        }
    }
}
