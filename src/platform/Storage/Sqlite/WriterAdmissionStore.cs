using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal sealed class WriterAdmissionStore(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal void RequireEditorAdmission(Guid operationId)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM configuration.configuration_edit_sessions WHERE session_id=$operation AND phase IN (0,1) AND writer_gate=0);";
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        if ((long)command.ExecuteScalar()! != 1) throw new IOException("configuration_editor_start_closed");
    }

    internal void CloseEditorAdmission(Guid operationId) => database.Write((connection, transaction) =>
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE configuration.configuration_edit_sessions SET writer_gate=1 WHERE session_id=$operation AND phase IN (0,1);";
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        if (command.ExecuteNonQuery() != 1) throw new IOException("configuration_editor_close_unbound");
        return true;
    });

    internal void RequireRunAdmission(Guid operationId)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM configuration.configuration_run_sessions s JOIN main.operations o ON o.operation_id=s.session_id " +
            "WHERE s.session_id=$operation AND s.phase IN (0,1) AND s.writer_gate=0 AND s.closed_elapsed_milliseconds IS NULL AND o.operation_kind='configuration.run.begin');";
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        if ((long)command.ExecuteScalar()! != 1) throw new IOException("configuration_writer_start_closed");
    }

    internal void CloseRunAdmission(Guid operationId, long elapsedMilliseconds, bool requireNoJobs = false)
    {
        if (elapsedMilliseconds is < 0 or > 922337203685477) throw new IOException("configuration_run_elapsed_invalid");
        database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE configuration.configuration_run_sessions SET writer_gate=1,closed_elapsed_milliseconds=$elapsed WHERE session_id=$operation AND phase IN (0,1) " +
                "AND (entered_running=1 OR $elapsed=0) AND ((writer_gate=0 AND closed_elapsed_milliseconds IS NULL) OR (writer_gate=1 AND closed_elapsed_milliseconds=$elapsed)) " +
                "AND $elapsed>=COALESCE((SELECT MAX(elapsed_milliseconds) FROM configuration.configuration_run_syncs WHERE session_id=$operation),0) " +
                (requireNoJobs ? "AND phase=0 AND entered_running=0 AND sync_ordinal=0 AND $elapsed=0 " +
                    "AND EXISTS(SELECT 1 FROM configuration.configuration_run_resources WHERE session_id=$operation) " +
                    "AND NOT EXISTS(SELECT 1 FROM configuration.configuration_run_resources r LEFT JOIN main.operation_journals j ON j.journal_id=r.journal_id " +
                    "WHERE r.session_id=$operation AND (j.phase IS NULL OR j.phase<>'committed' OR j.operation_id<>r.operation_id OR j.operation_kind<>'configExchange')) " +
                    "AND NOT EXISTS(SELECT 1 FROM configuration.configuration_run_resources r JOIN main.journal_writer_jobs b ON b.journal_id=r.journal_id WHERE r.session_id=$operation) " +
                    "AND NOT EXISTS(SELECT 1 FROM configuration.configuration_run_resources r JOIN main.journal_writer_job_roots b ON b.journal_id=r.journal_id WHERE r.session_id=$operation) " :
                    "AND EXISTS(SELECT 1 FROM configuration.configuration_run_resources WHERE session_id=$operation) " +
                    "AND NOT EXISTS(SELECT 1 FROM configuration.configuration_run_resources r WHERE r.session_id=$operation AND " +
                    "(SELECT count(*) FROM main.journal_writer_jobs b WHERE b.journal_id=r.journal_id)<>1) ") +
                "AND EXISTS(SELECT 1 FROM main.operations WHERE operation_id=$operation AND operation_kind='configuration.run.begin');";
            command.Parameters.AddWithValue("$operation", operationId.ToString("D")); command.Parameters.AddWithValue("$elapsed", elapsedMilliseconds);
            if (command.ExecuteNonQuery() != 1) throw new IOException("configuration_writer_close_unbound");
            return true;
        });
    }


    internal void CloseGroupAdmission(ConfigurationRunStatus session)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var state = AttemptWriterState.Read(instance, database, contracts, session.ParentJournalId);
        var step = state.Total.Steps.Single(step => step.StepId == session.StepId);
        if (step.Hooks is not { Count: > 0 } || state.Journal.OperationId != session.OwnerOperationId)
            throw new IOException("configuration_group_parent_unbound");
        var jobs = state.Jobs.Where(job => job.StepId == session.StepId).ToArray();
        StepWritersEvidence.RequireStopped(instance, database, contracts, state.Journal.JournalId, jobs);
        if (state.Gate.State == EffectGateState.Entered)
        {
            var suppressed = BindingSuppressionCodec.Read(state.Latest.Root, state.Total).SingleOrDefault(item => item.StepId == session.StepId);
            if (!jobs.Select(job => job.Role).Concat(suppressed?.Roles ?? []).Concat(RetryHookCodec.ResolvedRoles(state.Latest.Root, state.Total, step.StepId)).ToHashSet(StringComparer.Ordinal)
                .SetEquals(ExecutionBindingRoles.Required(step))) throw new IOException("configuration_group_dispatch_pending");
        }
        var configurationJobs = RegisteredGroupJobs(session, state, jobs);
        var primary = jobs.SingleOrDefault(job => job.Role == "process");
        var primaryName = configurationJobs.SingleOrDefault(job => job.Role == "process")?.JobName;
        if (session.PrimaryJobName != primaryName) throw new IOException("configuration_primary_job_unbound");
        var elapsed = primary?.ClosedElapsedMilliseconds ?? 0;
        if (primary is null && state.Gate.State == EffectGateState.Entered &&
            !BindingSuppressionCodec.Read(state.Latest.Root, state.Total).Any(item => item.StepId == session.StepId && item.Roles.Contains("process")))
            throw new IOException("configuration_primary_runtime_unbound");
        database.Write((connection, transaction) =>
        {
            AttemptWriterState.RequireCursor(connection, transaction, state.Journal, state.Gate.State);
            ExecutionStepAdmission.RequireJobsInside(connection, transaction, state.Journal.JournalId, state.AllJobs);
            RequireGroupJobsInside(connection, transaction, session, configurationJobs, requireClosures: true);
            using var command = ConfigurationRunRows.Command(connection, transaction, session.SessionId);
            command.CommandText = "UPDATE configuration.configuration_run_sessions SET writer_gate=1,closed_elapsed_milliseconds=$elapsed " +
                "WHERE session_id=$session AND owner_operation_id=$owner AND parent_journal_id=$parent AND step_id=$step AND revision=$revision AND phase IN (0,1) " +
                "AND ((writer_gate=0 AND closed_elapsed_milliseconds IS NULL) OR " +
                "(writer_gate=1 AND closed_elapsed_milliseconds=$elapsed)) " +
                "AND primary_job_name IS $primary AND $elapsed>=COALESCE((SELECT MAX(elapsed_milliseconds) FROM configuration.configuration_run_syncs WHERE session_id=$session),0);";
            command.Parameters.AddWithValue("$primary", (object?)primaryName ?? DBNull.Value);
            command.Parameters.AddWithValue("$elapsed", elapsed);
            command.Parameters.AddWithValue("$owner", session.OwnerOperationId.ToString("D")); command.Parameters.AddWithValue("$parent", session.ParentJournalId.ToString("D"));
            command.Parameters.AddWithValue("$step", session.StepId); command.Parameters.AddWithValue("$revision", session.Revision);
            if (command.ExecuteNonQuery() != 1) throw new IOException("configuration_group_close_unbound");
            return true;
        });
    }

    private IReadOnlyList<StepWriterState> RegisteredGroupJobs(ConfigurationRunStatus session, AttemptWriterState state,
        IReadOnlyList<StepWriterState> jobs)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT job_name FROM main.journal_writer_jobs WHERE journal_id=$journal LIMIT 5;";
        command.Parameters.AddWithValue("$journal", session.Resources[0].JournalId.ToString("D"));
        var names = new HashSet<string>(StringComparer.Ordinal);
        using (var rows = command.ExecuteReader()) while (rows.Read()) names.Add(rows.GetString(0));
        if (names.Any(name => !jobs.Any(job => job.JobName == name && job.Role != "judgement")))
            throw new IOException("configuration_group_job_unbound");
        var closures = new JobClosureStore(instance, database, contracts);
        foreach (var job in jobs.Where(job => job.Role != "judgement" && !names.Contains(job.JobName)))
        {
            var closure = closures.ReadForExecutionEvidence(state.Journal.JournalId, job.WriterId, job.JobName, job.SessionId)
                ?? throw new IOException("writer_job_closure_missing");
            if (closure.WasExecuted || !closure.Family.IsEmpty || closure.RunElapsedMilliseconds != 0)
                throw new IOException("configuration_unregistered_writer_effect_unverified");
        }
        return jobs.Where(job => names.Contains(job.JobName)).ToArray();
    }

    private static void RequireGroupJobsInside(SqliteConnection connection, SqliteTransaction transaction, ConfigurationRunStatus session,
        IReadOnlyList<StepWriterState> expected, bool requireClosures)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        foreach (var resource in session.Resources)
        {
            command.Parameters.Clear(); command.Parameters.AddWithValue("$journal", resource.JournalId.ToString("D"));
            command.CommandText = "SELECT b.writer_id,b.job_name,b.session_id,c.job_name FROM main.journal_writer_jobs b LEFT JOIN main.journal_writer_job_closures c " +
                "ON c.journal_id=b.journal_id AND c.writer_id=b.writer_id AND c.job_name=b.job_name WHERE b.journal_id=$journal LIMIT 5;";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                if (!seen.Add(rows.GetString(1)) || !expected.Any(job => job.WriterId.ToString("D") == rows.GetString(0) &&
                    job.JobName == rows.GetString(1) && job.SessionId == rows.GetInt32(2)) || requireClosures && rows.IsDBNull(3))
                    throw new IOException("configuration_group_job_unbound");
            }
            if (seen.Count != expected.Count) throw new IOException("configuration_group_job_unbound");
        }
    }

}
