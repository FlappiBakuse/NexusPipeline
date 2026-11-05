using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Storage.Configuration;

internal static class ConfigurationRunInventory
{
    internal static void VerifyReadOnly(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
    {
        try
        {
            using var parents = JournalReadScope.HoldParents(instance, contracts);
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT s.session_id,o.subject_id,o.operation_kind,o.status FROM configuration_run_sessions s " +
                "JOIN operations o ON o.operation_id=s.session_id LIMIT 4097;";
            var items = new List<(Guid Id, string Subject, string Kind, string Status)>();
            using (var row = command.ExecuteReader())
                while (row.Read()) items.Add((Guid.Parse(row.GetString(0)), row.GetString(1), row.GetString(2), row.GetString(3)));
            if (items.Count > 4096) throw new IOException("configuration_run_inventory_limit");
            foreach (var item in items)
            {
                var session = ConfigurationRunRows.Read(connection, transaction, item.Id, item.Subject)!;
                if (item.Kind != "configuration.run.begin" ||
                    item.Status != (session.Phase == ConfigurationRunPhase.Prepared ? "accepted" : "completed"))
                    throw new IOException("configuration_run_operation_unbound");
                var binding = ConfigurationRunBinding.Read(instance, database, contracts, session);
                VerifyWriterAdmission(instance, database, contracts, connection, transaction, session, binding);
                ConfigurationRunHistory.Verify(connection, transaction, session, item.Subject,
                    binding.Resources.ToDictionary(value => value.State.Source.Address, value => value.State), new(new(instance, contracts)));
                foreach (var resource in session.Resources) VerifyResource(connection, transaction, session, resource, item.Subject,
                    binding.Resources.Single(value => value.State.Source.Address == resource.Address).State, new(new(instance, contracts)));
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM operations WHERE operation_id=$owner AND subject_id=$subject);";
                command.Parameters.Clear(); command.Parameters.AddWithValue("$owner", session.OwnerOperationId.ToString("D")); command.Parameters.AddWithValue("$subject", item.Subject);
                if ((long)command.ExecuteScalar()! != 1 || binding.Parent.OperationId != session.OwnerOperationId.ToString("D"))
                    throw new IOException("configuration_run_parent_subject_unbound");
            }
            command.Parameters.Clear();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM configuration_run_sync_items i LEFT JOIN configuration_run_syncs s ON s.operation_id=i.operation_id " +
                "LEFT JOIN configuration_run_sessions g ON g.completion_operation_id=i.operation_id WHERE s.operation_id IS NULL AND g.session_id IS NULL);";
            if ((long)command.ExecuteScalar()! != 0) throw new IOException("configuration_run_sync_result_orphaned");
        }
        catch (Exception error) when (error is ConfigurationException or ArgumentException or InvalidOperationException or FormatException)
        { throw new IOException("configuration_run_metadata_unrecognized", error); }
    }

    private static void VerifyWriterAdmission(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        SqliteConnection connection, SqliteTransaction transaction, ConfigurationRunStatus session, BoundConfigurationRun binding)
    {
        if (session.IsWriterAdmissionClosed != session.ClosedElapsedMilliseconds.HasValue)
            throw new IOException("configuration_run_elapsed_unbound");
        using var command = ConfigurationRunRows.Command(connection, transaction, session.SessionId);
        command.CommandText = "SELECT r.journal_id,b.writer_id,b.job_name,b.session_id FROM configuration_run_resources r LEFT JOIN journal_writer_jobs b " +
            "ON b.journal_id=r.journal_id WHERE r.session_id=$session ORDER BY r.journal_id LIMIT 98;";
        var jobs = new List<(Guid Journal, Guid Writer, string Job, int Session)>();
        using (var rows = command.ExecuteReader())
            while (rows.Read())
                if (!rows.IsDBNull(1)) jobs.Add((Guid.Parse(rows.GetString(0)), Guid.Parse(rows.GetString(1)), rows.GetString(2), rows.GetInt32(3)));
        if (jobs.Count == 0)
        {
            if (session.PrimaryJobName is not null || session.EnteredRunning || session.SyncOrdinal != 0 || session.Phase is not (ConfigurationRunPhase.Prepared or ConfigurationRunPhase.Interrupted))
                throw new IOException("configuration_run_job_unbound");
            if (!session.IsWriterAdmissionClosed) return;
            if (session.ClosedElapsedMilliseconds != 0) throw new IOException("configuration_run_elapsed_unbound");
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM configuration_run_resources r JOIN journal_writer_job_roots b ON b.journal_id=r.journal_id WHERE r.session_id=$session);";
            if ((long)command.ExecuteScalar()! != 0) throw new IOException("configuration_run_job_unbound");
            foreach (var resource in session.Resources)
            {
                var physical = new JournalStore(instance, contracts).Read(resource.JournalId);
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM operation_journals WHERE journal_id=$journal AND phase='committed' AND revision=$revision);";
                command.Parameters.Clear(); command.Parameters.AddWithValue("$journal", resource.JournalId.ToString("D"));
                command.Parameters.AddWithValue("$revision", physical.Revision);
                if (physical.Phase != JournalPhase.Committed || (long)command.ExecuteScalar()! != 1 ||
                    physical.Root.GetProperty("participants").GetArrayLength() != 0 || !physical.Root.GetProperty("receipts").EnumerateArray().Any(receipt =>
                        receipt.GetProperty("stepId").GetString() == "verify-no-effect" && receipt.GetProperty("effect").GetString() == "notExecuted"))
                    throw new IOException("configuration_run_no_job_effects_unbound");
            }
            return;
        }
        if (binding.Step.Hooks is { Count: > 0 })
        {
            var parent = AttemptWriterState.Read(instance, database, contracts, session.ParentJournalId);
            var candidates = parent.Jobs.Where(job => job.StepId == session.StepId && job.Role != "judgement").ToArray();
            var first = jobs.Where(job => job.Journal == session.Resources[0].JournalId).Select(job => (job.Writer, job.Job, job.Session)).ToHashSet();
            if (first.Count is < 1 or > 3 || jobs.Count != first.Count * session.Resources.Length ||
                session.Resources.Any(resource => !first.SetEquals(jobs.Where(job => job.Journal == resource.JournalId).Select(job => (job.Writer, job.Job, job.Session)))) ||
                first.Any(item => !candidates.Any(job => job.WriterId == item.Writer && job.JobName == item.Job && job.SessionId == item.Session)) ||
                session.PrimaryJobName != candidates.SingleOrDefault(job => job.Role == "process" && first.Any(item => item.Job == job.JobName))?.JobName)
                throw new IOException("configuration_group_job_unbound");
            if (session.IsWriterAdmissionClosed && session.CompletionOperationId.HasValue)
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                _ = new RunStopEvidence(instance, database, contracts).ReadRestored(session);
            }
            return;
        }
        if (session.PrimaryJobName != jobs[0].Job)
            throw new IOException("configuration_primary_job_unbound");
        if (jobs.Count != session.Resources.Length || jobs.Select(item => item.Journal).Distinct().Count() != session.Resources.Length ||
            jobs.Select(item => (item.Writer, item.Job, item.Session)).Distinct().Count() != 1)
            throw new IOException("configuration_run_job_unbound");
        if (!session.CompletionOperationId.HasValue) return;
        var closures = new JobClosureStore(instance, database, contracts);
        foreach (var job in jobs)
            if (closures.Read(job.Journal, job.Writer, job.Job, job.Session) is null) throw new IOException("writer_job_closure_missing");
    }

    private static void VerifyResource(SqliteConnection connection, SqliteTransaction transaction, ConfigurationRunStatus session,
        RunResourceStatus resource, string subject, FrozenRunState state, ConfigurationSnapshotCodec snapshots)
    {
        var source = state.Source;
        using var command = ConfigurationRunRows.Command(connection, transaction, session.SessionId);
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM operations WHERE operation_id=$child AND subject_id=$subject " +
            "AND operation_kind='configuration.run.resource' AND request_digest=$digest AND status=$status);";
        command.Parameters.AddWithValue("$child", resource.OperationId.ToString("D")); command.Parameters.AddWithValue("$subject", subject);
        command.Parameters.AddWithValue("$digest", resource.PlanDigest);
        command.Parameters.AddWithValue("$status", session.CompletionOperationId.HasValue ? "completed" : "accepted");
        if ((long)command.ExecuteScalar()! != 1)
            throw new IOException("configuration_run_resource_operation_unbound");
        if (resource.ExpectedRevision != state.InitialPublicationRevision) throw new IOException("configuration_run_publication_cursor_unbound");
        var initial = ConfigurationRevisionRows.Read(connection, transaction, resource.Address, resource.ExpectedRevision, snapshots).Revision;
        if (initial.UserId != source.UserId || initial.ScriptId != source.ScriptId || initial.Contract != source.Contract || initial.Tree.Shape != source.Tree.Shape)
            throw new IOException("configuration_run_resource_baseline_unbound");
        var saved = ConfigurationRevisionRows.Read(connection, transaction, resource.Address, source.Revision, snapshots).Revision;
        if (saved.UserId != source.UserId || saved.ScriptId != source.ScriptId || saved.Contract != source.Contract || saved.Tree.Shape != source.Tree.Shape ||
            !saved.Tree.Entries.SequenceEqual(source.Tree.Entries))
            throw new IOException("configuration_run_resource_baseline_unbound");
        var current = ConfigurationRevisionRows.Read(connection, transaction, resource.Address, resource.CurrentRevision, snapshots).Revision;
        if (current.UserId != source.UserId || current.ScriptId != source.ScriptId || current.Contract != source.Contract)
            throw new IOException("configuration_run_resource_cursor_unbound");
        if (!session.CompletionOperationId.HasValue && ConfigurationRevisionRows.Current(connection, transaction, resource.Address, snapshots)?.Revision.Revision != resource.CurrentRevision)
            throw new IOException("configuration_run_current_pointer_conflict");
        command.CommandText = "SELECT phase FROM operation_journals WHERE journal_id=$journal;";
        command.Parameters.AddWithValue("$journal", resource.JournalId.ToString("D"));
        if (session.CompletionOperationId.HasValue && command.ExecuteScalar() as string != "committed") throw new IOException("configuration_run_completion_not_restored");
    }
}
