using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class AttemptTerminalEvidence
{
    internal static void RequireHookResultsInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument document)
    {
        RetryHookProjection.RequireSavedInside(connection, transaction, state, document);
        var jobs = WriterIntentCodec.ReadJobs(document.Root, state.Total);
        var hooks = HookResultCodec.Read(document.Root, state.Total);
        var suppressed = BindingSuppressionCodec.Read(document.Root, state.Total);
        foreach (var item in suppressed.Where(item => item.DispatchOperationId is not null))
        {
            var close = ExecutionCancellationRows.Read(connection, transaction, state.Journal, state.Attempt);
            if (close is null || close.Identity.OperationId != item.DispatchOperationId || close.ReasonCode != item.ReasonCode)
                throw new IOException("execution_binding_suppression_unbound");
        }
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        foreach (var item in hooks)
        {
            var job = jobs.Single(job => job.StepId == item.StepId && job.Role == item.Role);
            command.Parameters.Clear(); command.Parameters.AddWithValue("$journal", state.Journal.JournalId.ToString("D"));
            command.Parameters.AddWithValue("$writer", job.WriterId.ToString("D")); command.Parameters.AddWithValue("$job", job.JobName);
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM journal_writer_job_closures WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job);";
            if ((long)command.ExecuteScalar()! != 1) throw new IOException("writer_job_closure_missing");
        }
    }

    internal static ImmutableArray<ExecutionStepFact> Steps(AttemptWriterState state) =>
        StepResultCodec.CompletedFacts(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);

    internal static void RequireArtifacts(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        AttemptWriterState state)
    {
        if (OperatingSystem.IsWindows()) PreviewDomainEvidence.RequireStopped(database, state);
        _ = new TargetJobEvidence(instance, database, contracts).RequireAll(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        _ = new ProviderLauncherEvidence(instance, database, contracts).RequireAll(state, verifyContents: true);
        _ = new PcStageEvidence(instance, database, contracts).RequireAll(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        var android = new AndroidResultEvidence(instance, database, contracts);
        foreach (var startup in AndroidStartupCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256))
            _ = android.RequireStartup(state, startup, verifyContent: true);
        foreach (var collected in AndroidCollectedCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256))
            _ = android.RequireCollected(state, collected, verifyContent: true);
        if (OperatingSystem.IsWindows())
            foreach (var step in state.Total.Steps.Where(step => step.Target?.Kind == "android"))
                new AndroidControlEvidence(instance, database, contracts)
                    .RequireStopped(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256, step.StepId);
        if (OperatingSystem.IsWindows())
            foreach (var capture in ExecutionCaptureCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256).Where(value => value.Result is not null))
                new CaptureEvidenceReader(instance, database, contracts).RequireResolvedForRecovery(state.Journal, state.Attempt, state.Total,
                    new JournalStore(instance, contracts).ReadAllRevisions(state.Journal.JournalId), capture.Binding.Primary.StepId);
        _ = new RetryHookEvidence(instance, database, contracts).RequireAll(state.Attempt, state.Total, state.Latest.Root, verifyContents: true);
        foreach (var hook in HookResultCodec.Read(state.Latest.Root, state.Total))
        {
            var read = new HookEvidenceReader(instance, database, contracts).Read(hook.Binding, hook.Role, hook.Artifact);
            if (read.Engine != hook.Engine || read.ReasonCode != hook.ReasonCode) throw new IOException("execution_hook_evidence_unbound");
        }
        foreach (var result in StepResultCodec.Read(state.Latest.Root, state.Total))
        {
            var read = new StepEvidenceReader(instance, database, contracts).Read(result.Binding, result.Latest.Artifact, result.IsCollected);
            if (read.Artifact != result.Latest.Artifact || read.Engine != result.Latest.Engine || read.Business != result.Latest.Business ||
                read.Generation != result.Latest.Generation || read.ReasonCode != result.Latest.ReasonCode)
                throw new IOException("execution_step_evidence_unbound");
        }
    }

    internal static void RequireStopped(StateDatabase database, Guid journalId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var observed = new JournalWriterStore(database).ObserveForCurrentExecutor(journalId);
        if (!observed.IsComplete || observed.State != WriterState.Stopped) throw new IOException("attempt_writer_stop_unconfirmed");
    }

    internal static void RequireCollected(StateDatabase database, AttemptWriterState state)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        RequireCollectedInside(connection, transaction, state, state.Latest); transaction.Commit();
    }

    internal static void RequireCollectedInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument document)
    {
        if (!state.IsExecutionStart || state.Gate.State != EffectGateState.Entered ||
            DispatchIntentCodec.Read(document.Root, state.Total) is null) throw new IOException("attempt_workers_uncollected");
        TargetJobProjection.RequireResolved(state, document);
        ProviderLauncherCodec.RequireResolved(document.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        AndroidCollectedCodec.RequireResolved(document.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        ExecutionCaptureCodec.RequireResolved(document.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        PreviewDomainCodec.RequireResolved(document.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        var jobs = WriterIntentCodec.ReadAllJobs(document.Root, state.Total);
        var primary = WriterIntentCodec.Read(document.Root, state.Total);
        var results = StepResultCodec.Read(document.Root, state.Total);
        if (StepResultCodec.Coverage(document.Root, state.Total).Any(item => item.Kind is not (StepCompletionKind.Collected or StepCompletionKind.NotDispatched)) ||
            results.Length != primary.Length || results.Any(result => !result.IsCollected) ||
            jobs.Any(job => job.ClosedElapsedMilliseconds is null)) throw new IOException("attempt_workers_uncollected");
        ExecutionStepAdmission.RequireUndispatchedInside(connection, transaction, state, document);
        RequireHookResultsInside(connection, transaction, state, document);
        _ = ExecutionCancellationRows.Read(connection, transaction, state.Journal, state.Attempt) ?? throw new IOException("execution_dispatch_open");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM journal_writer_jobs WHERE journal_id=$journal;";
        command.Parameters.AddWithValue("$journal", state.Journal.JournalId.ToString("D"));
        if ((long)command.ExecuteScalar()! != jobs.Length) throw new IOException("execution_writer_jobs_unbound");
        foreach (var job in jobs)
        {
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM journal_writer_job_closures c JOIN journal_writer_jobs b " +
                "ON b.journal_id=c.journal_id AND b.writer_id=c.writer_id AND b.job_name=c.job_name " +
                "WHERE c.journal_id=$journal AND c.writer_id=$writer AND c.job_name=$job AND b.session_id=$session);";
            command.Parameters.AddWithValue("$writer", job.WriterId.ToString("D")); command.Parameters.AddWithValue("$job", job.JobName);
            command.Parameters.AddWithValue("$session", job.SessionId);
            if ((long)command.ExecuteScalar()! != 1) throw new IOException("writer_job_closure_missing");
            foreach (var key in new[] { "$writer", "$job", "$session" }) command.Parameters.RemoveAt(key);
        }
    }

    internal static void RequireRestoredInside(SqliteConnection connection, SqliteTransaction transaction, AttemptWriterState state,
        bool requireCompletedGroups = true, bool allowUnregistered = false, string? stepId = null)
    {
        if (!IsRestoredInside(connection, transaction, state, requireCompletedGroups, allowUnregistered, stepId))
            throw new IOException("attempt_configuration_not_restored");
    }

    internal static bool IsRestoredInside(SqliteConnection connection, SqliteTransaction transaction, AttemptWriterState state,
        bool requireCompletedGroups = true, bool allowUnregistered = false, string? stepId = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var expected = 0; var restored = true;
        foreach (var child in ExecutionInvocationCodec.Plans(state.Latest.Root, state.Total, state.Attempt).Where(child => stepId is null || child.StepId == stepId))
        {
            var plan = AttemptPlanCodec.Reference(child.Plan);
            command.Parameters.Clear(); command.Parameters.AddWithValue("$path", plan.RelativePath); command.Parameters.AddWithValue("$digest", plan.Sha256);
            command.CommandText = "SELECT count(*) FROM operation_journals WHERE plan_path=$path AND plan_digest=$digest;";
            var count = (long)command.ExecuteScalar()!;
            var undispatched = BindingSkipCodec.Read(state.Latest.Root, state.Total).Any(item => item.StepId == child.StepId) ||
                StepResultCodec.ReadUndispatched(state.Latest.Root, state.Total)?.StepIds.Contains(child.StepId) == true;
            if ((allowUnregistered || undispatched) && count == 0) continue;
            if (count != 1) throw new IOException("attempt_configuration_membership_unbound");
            expected++;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM configuration_run_resources r JOIN configuration_run_sessions s ON s.session_id=r.session_id " +
                "JOIN operation_journals j ON j.journal_id=r.journal_id AND j.operation_id=r.operation_id " +
                "WHERE s.owner_operation_id=$owner AND s.parent_journal_id=$parent AND s.step_id=$step " +
                "AND ($complete=0 OR (s.phase IN (2,3) AND s.writer_gate=1 AND s.closed_elapsed_milliseconds IS NOT NULL AND s.completion_operation_id IS NOT NULL)) " +
                "AND r.plan_path=$path AND r.plan_digest=$digest AND j.phase='committed' AND j.operation_kind='configExchange');";
            command.Parameters.Clear(); command.Parameters.AddWithValue("$owner", state.Journal.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$parent", state.Journal.JournalId.ToString("D")); command.Parameters.AddWithValue("$step", child.StepId);
            command.Parameters.AddWithValue("$path", plan.RelativePath); command.Parameters.AddWithValue("$digest", plan.Sha256);
            command.Parameters.AddWithValue("$complete", requireCompletedGroups);
            if ((long)command.ExecuteScalar()! != 1) restored = false;
        }
        command.Parameters.Clear(); command.Parameters.AddWithValue("$owner", state.Journal.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$parent", state.Journal.JournalId.ToString("D"));
        command.Parameters.AddWithValue("$selectedStep", (object?)stepId ?? DBNull.Value);
        command.CommandText = "SELECT count(*) FROM configuration_run_resources r JOIN configuration_run_sessions s ON s.session_id=r.session_id " +
            "WHERE (s.owner_operation_id=$owner OR s.parent_journal_id=$parent) AND ($selectedStep IS NULL OR s.step_id=$selectedStep);";
        if ((long)command.ExecuteScalar()! != expected) throw new IOException("attempt_configuration_membership_unbound");
        return restored;
    }
}
