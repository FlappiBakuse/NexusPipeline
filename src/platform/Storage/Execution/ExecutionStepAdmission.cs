using System.Text.Json;
using Microsoft.Data.Sqlite;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionStepAdmission
{
    internal static void RequireRequiredRoles(NexusPipeline.Platform.Serialization.Protocol.ExecutionPlan total,
        IReadOnlyList<StepWriterState> jobs, string stepId, string role, JsonElement journal)
    {
        var step = total.Steps.Single(item => item.StepId == stepId);
        var current = jobs.Where(item => item.StepId == stepId).ToArray();
        var required = ExecutionBindingRoles.Required(step);
        var resolved = RetryHookCodec.ResolvedRoles(journal, total, stepId);
        if (!required.Contains(role) || resolved.Contains(role) || current.Count(item => item.Role == role) != 1)
            throw new IOException("execution_writer_roles_unprepared");
        if (role is "process" or "judgement")
        {
            if (current.Count(item => item.Role == "process") != 1 ||
                current.Count(item => item.Role == "judgement") != (step.JudgementWorker is null ? 0 : 1) ||
                required.Contains("preHook") && !current.Any(item => item.Role == "preHook" && item.ClosedElapsedMilliseconds.HasValue) &&
                    !resolved.Contains("preHook"))
                throw new IOException("execution_writer_roles_unprepared");
        }
        if (role == "postHook" && (!current.Any(item => item.Role == "process" && item.ClosedElapsedMilliseconds.HasValue) ||
            current.Where(item => item.Role != "postHook").Any(item => !item.ClosedElapsedMilliseconds.HasValue)))
            throw new IOException("execution_writer_roles_unprepared");
    }

    internal static void RequireOpenRoles(NexusPipeline.Platform.Serialization.Protocol.ExecutionPlan total,
        IReadOnlyList<StepWriterState> jobs, string stepId, string role, JsonElement journal)
    {
        RequireRequiredRoles(total, jobs, stepId, role, journal);
        if (jobs.Any(item => item.StepId == stepId && (item.Role == role ||
            role is "process" or "judgement" && item.Role is "process" or "judgement") && item.ClosedElapsedMilliseconds.HasValue))
            throw new IOException("execution_writer_start_closed");
    }

    internal static int RequireNext(JournalDocument document, NexusPipeline.Platform.Serialization.Protocol.ExecutionPlan total, string stepId, string role = "process")
    {
        var all = WriterIntentCodec.ReadJobs(document.Root, total);
        var dispatched = BindingSkipCodec.Processed(document.Root, total, all);
        var ordinal = dispatched.LastOrDefault() == stepId ? dispatched.Length - 1 : dispatched.Length;
        if (DispatchIntentCodec.Read(document.Root, total) is not null ||
            StepResultCodec.ReadUndispatched(document.Root, total) is not null || ordinal < 0 || ordinal >= total.Steps.Count ||
            all.Length >= JournalCapacityLimits.MaximumWriterJobs || total.Steps[ordinal].StepId != stepId ||
            document.Phase is not (JournalPhase.Prepared or JournalPhase.Applying) ||
            all.Length != 0 && ordinal != 0 && document.Phase != JournalPhase.Applying || all.Any(job => job.StepId == stepId && job.Role == role) ||
            BindingSuppressionCodec.Read(document.Root, total).Any(item => item.StepId == stepId))
            throw new IOException("execution_writer_registration_closed");
        var input = total.Steps[ordinal];
        var required = ExecutionBindingRoles.Required(input);
        var current = all.Where(job => job.StepId == stepId).ToArray();
        var roleIndex = required.IndexOf(role);
        if (RetryHookCodec.ResolvedRoles(document.Root, total, stepId).Contains(role)) throw new IOException("execution_writer_registration_closed");
        if (roleIndex < 0 || current.Any(job => required.IndexOf(job.Role) >= roleIndex))
            throw new IOException("execution_writer_role_unbound");
        if (role is "process" or "judgement" && required.Contains("preHook") &&
            !HookResultCodec.Read(document.Root, total).Any(item => item.StepId == stepId && item.Role == "preHook" && item.Engine == EngineOutcome.Succeeded) &&
            !RetryHookCodec.ResolvedRoles(document.Root, total, stepId).Contains("preHook"))
            throw new IOException("execution_pre_hook_incomplete");
        if (role == "judgement" && !current.Any(job => job.Role == "process" && job.ClosedElapsedMilliseconds is null))
            throw new IOException("execution_writer_roles_unprepared");
        if (role == "postHook")
            foreach (var capture in document.Root.GetProperty("participants").EnumerateArray().Where(ExecutionCaptureCodec.IsParticipant)
                .Select(ExecutionCaptureCodec.Decode).Where(value => value.Binding.Primary.StepId == stepId))
                ExecutionCaptureCodec.RequireResolved(document.Root, total, capture.Binding.TotalPlanSha256, stepId);
        if (role == "postHook")
            foreach (var domain in document.Root.GetProperty("participants").EnumerateArray().Where(PreviewDomainCodec.IsParticipant)
                .Select(PreviewDomainCodec.Decode).Where(value => value.Source.Primary.StepId == stepId))
                PreviewDomainCodec.RequireResolved(document.Root, total, domain.Source.TotalPlanSha256, stepId);
        if (role == "postHook" && (current.Any(job => job.ClosedElapsedMilliseconds is null) ||
            !StepResultCodec.Read(document.Root, total).Any(result => result.Binding.StepId == stepId && result.IsCollected &&
                result.Latest.Engine != EngineOutcome.NotStarted)))
            throw new IOException("execution_post_hook_admission_closed");
        RequirePredecessors(document, total, ordinal);
        return ordinal;
    }

    internal static void RequirePredecessors(JournalDocument document, NexusPipeline.Platform.Serialization.Protocol.ExecutionPlan total, int ordinal)
    {
        var all = WriterIntentCodec.ReadJobs(document.Root, total);
        var dispatched = BindingSkipCodec.Processed(document.Root, total, all);
        if (ordinal < 0 || ordinal >= total.Steps.Count || dispatched.Length < ordinal)
            throw new IOException("execution_writer_step_unbound");
        foreach (var step in total.Steps.Take(ordinal))
            if (!ExecutionBindingRoles.IsComplete(document.Root, total, step.StepId))
                throw new IOException("execution_predecessor_uncollected");
    }

    [SupportedOSPlatform("windows")]
    internal static IDisposable HoldPredecessors(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        AttemptWriterState state, int ordinal)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        RequirePredecessors(state.Latest, state.Total, ordinal);
        AttemptTerminalEvidence.RequireArtifacts(instance, database, contracts, state);
        var closures = new JobClosureStore(instance, database, contracts);
        var priorSteps = state.Total.Steps.Take(ordinal).Select(step => step.StepId).ToHashSet(StringComparer.Ordinal);
        foreach (var job in state.Jobs.Where(job => priorSteps.Contains(job.StepId)))
        {
            var closure = closures.ReadForExecutionEvidence(state.Journal.JournalId, job.WriterId, job.JobName, job.SessionId)
                ?? throw new IOException("writer_job_closure_missing");
            if (closure.RunElapsedMilliseconds != job.ClosedElapsedMilliseconds ||
                closure.Family.Any(member => WriterStopEvidence.ObserveProcess(member) != WriterState.Stopped))
                throw new IOException("execution_predecessor_stop_unconfirmed");
        }
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            RequirePredecessorsInside(connection, transaction, state, state.Latest, ordinal); transaction.Commit();
        }
        return RunBindingEvidence.HoldRestored(instance, database, contracts, state,
            state.Total.Steps.Take(ordinal).Select(step => step.StepId));
    }

    internal static void RequirePredecessorsInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument document, int ordinal)
    {
        RequirePredecessors(document, state.Total, ordinal);
        var jobs = WriterIntentCodec.ReadJobs(document.Root, state.Total);
        RequireJobsInside(connection, transaction, state.Journal.JournalId, WriterIntentCodec.ReadAllJobs(document.Root, state.Total));
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var priorSteps = state.Total.Steps.Take(ordinal).Select(step => step.StepId).ToHashSet(StringComparer.Ordinal);
        foreach (var job in jobs.Where(job => priorSteps.Contains(job.StepId)))
        {
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.journal_writer_job_closures WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job);";
            command.Parameters.Clear(); command.Parameters.AddWithValue("$journal", state.Journal.JournalId.ToString("D"));
            command.Parameters.AddWithValue("$writer", job.WriterId.ToString("D")); command.Parameters.AddWithValue("$job", job.JobName);
            if ((long)command.ExecuteScalar()! != 1) throw new IOException("writer_job_closure_missing");
        }
        foreach (var step in state.Total.Steps.Take(ordinal))
        {
            ExecutionCaptureCodec.RequireResolved(document.Root, state.Total, state.Attempt.ExecutionPlan.Sha256, step.StepId);
            PreviewDomainCodec.RequireResolved(document.Root, state.Total, state.Attempt.ExecutionPlan.Sha256, step.StepId);
            ProviderLauncherCodec.RequireResolved(document.Root, state.Total, state.Attempt.ExecutionPlan.Sha256, step.StepId);
            AttemptTerminalEvidence.RequireRestoredInside(connection, transaction, state, stepId: step.StepId);
        }
    }

    internal static void RequireJobsInside(SqliteConnection connection, SqliteTransaction transaction, Guid journalId,
        IReadOnlyList<StepWriterState> jobs)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"SELECT writer_id,job_name,session_id FROM main.journal_writer_jobs WHERE journal_id=$journal LIMIT {JournalCapacityLimits.MaximumWriterJobs + 1};";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var row = command.ExecuteReader(); var count = 0;
        while (row.Read())
        {
            count++;
            if (count > JournalCapacityLimits.MaximumWriterJobs || !jobs.Any(job => job.WriterId.ToString("D") == row.GetString(0) && job.JobName == row.GetString(1) && job.SessionId == row.GetInt32(2)))
                throw new IOException("execution_writer_jobs_unbound");
        }
        if (count != jobs.Count) throw new IOException("execution_writer_jobs_unbound");
    }

    internal static void RequireUndispatchedInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument document)
    {
        var suffix = StepResultCodec.ReadUndispatched(document.Root, state.Total);
        if (suffix is null) return;
        var accepted = ExecutionCancellationRows.Read(connection, transaction, state.Journal, state.Attempt);
        if (accepted is null || accepted.Identity.OperationId != suffix.DispatchOperationId || accepted.ReasonCode != suffix.ReasonCode)
            throw new IOException("execution_undispatched_unbound");
        RequireJobsInside(connection, transaction, state.Journal.JournalId, WriterIntentCodec.ReadAllJobs(document.Root, state.Total));
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        foreach (var stepId in suffix.StepIds)
        {
            command.Parameters.Clear(); command.Parameters.AddWithValue("$owner", state.Journal.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$parent", state.Journal.JournalId.ToString("D")); command.Parameters.AddWithValue("$step", stepId);
            command.Parameters.AddWithValue("$invocation", ExecutionInvocationCodec.Ordinal(document.Root, stepId));
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM configuration.configuration_run_sessions s JOIN configuration.configuration_run_resources r USING(session_id) " +
                "JOIN main.journal_writer_jobs b ON b.journal_id=r.journal_id WHERE (s.owner_operation_id=$owner OR s.parent_journal_id=$parent) AND s.step_id=$step AND s.invocation_ordinal=$invocation);";
            if ((long)command.ExecuteScalar()! != 0) throw new IOException("execution_undispatched_writer_observed");
        }
    }
}
