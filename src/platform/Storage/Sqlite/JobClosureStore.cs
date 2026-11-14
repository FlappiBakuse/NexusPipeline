using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal sealed class JobClosureStore(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal void RecordRoot(Guid operationId, IReadOnlyList<Guid> journals, string jobName, ProcessIdentity root) => RecordRoot(operationId, journals, jobName, root, run: false);
    internal void RecordRunRoot(Guid operationId, IReadOnlyList<Guid> journals, string jobName, ProcessIdentity root, AttemptWriterState? parent = null) =>
        RecordRoot(operationId, journals, jobName, root, run: true, parent);

    private void RecordRoot(Guid operationId, IReadOnlyList<Guid> journals, string jobName, ProcessIdentity root, bool run, AttemptWriterState? parent = null)
    {
        var writer = JournalWriterStore.CurrentWriter;
        database.Write((connection, transaction) =>
        {
            if (parent is not null)
            {
                AttemptWriterState.RequireCursor(connection, transaction, parent.Journal, EffectGateState.Entered);
                ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, parent.Journal.JournalId);
                var job = parent.Jobs.SingleOrDefault(job => job.JobName == jobName && job.Role != "judgement") ??
                    throw new IOException("configuration_group_root_unbound");
                using var bound = Key(connection, transaction, parent.Journal.JournalId, writer.WriterId, jobName);
                bound.CommandText = "SELECT EXISTS(SELECT 1 FROM main.journal_writer_job_roots WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job " +
                    "AND process_id=$pid AND created_file_time=$created AND image_path=$path AND image_sha256=$hash AND user_sid=$sid AND elevated=$elevated);";
                ProcessParameters(bound, root);
                if (job.WriterId != writer.WriterId || (long)bound.ExecuteScalar()! != 1) throw new IOException("configuration_group_root_unbound");
            }
            foreach (var id in journals)
            {
                using var command = Key(connection, transaction, id, writer.WriterId, jobName);
                var owner = run ? "EXISTS(SELECT 1 FROM main.operation_journals j JOIN configuration.configuration_run_resources r ON r.journal_id=j.journal_id AND r.operation_id=j.operation_id " +
                    "JOIN configuration.configuration_run_sessions s ON s.session_id=r.session_id JOIN main.operations o ON o.operation_id=s.session_id WHERE j.journal_id=$journal " +
                    "AND j.operation_kind='configExchange' AND j.phase='applied' AND s.session_id=$operation AND s.phase=1 AND s.writer_gate=0 AND o.operation_kind='configuration.run.begin') " :
                    "EXISTS(SELECT 1 FROM main.operation_journals j JOIN configuration.configuration_edit_sessions s ON s.session_id=j.operation_id JOIN main.operations o ON o.operation_id=s.session_id " +
                    "WHERE j.journal_id=$journal AND j.operation_id=$operation AND j.operation_kind='configEdit' AND j.phase='applied' AND s.phase=1 AND s.writer_gate=0 " +
                    "AND o.operation_kind='configuration.edit.begin') ";
                command.CommandText = "INSERT INTO main.journal_writer_job_roots SELECT $journal,$writer,$job,$pid,$created,$path,$hash,$sid,$elevated WHERE " + owner +
                    "AND NOT EXISTS(SELECT 1 FROM main.journal_writer_job_closures WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job);";
                command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
                ProcessParameters(command, root);
                if (command.ExecuteNonQuery() != 1) throw new IOException("writer_job_root_registration_unbound");
            }
            return true;
        });
    }

    internal BlobReference RecordClosed(FrozenJobClosure closure)
    {
        var attempt = ReadAttemptBinding(closure);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var reference = new JobClosureCodec(instance, contracts, blobs).Write(closure);
        using var plans = StableDirectoryLease.Acquire(Path.Combine(instance.RuntimeRoot, "blobs/plans"));
        using var held = blobs.OpenVerified(reference);
        database.Write((connection, transaction) =>
        {
            foreach (var id in closure.JournalIds)
            {
                VerifyBinding(connection, transaction, closure, id, closure.WriterId, closure.JobName, closure.SessionId, attempt);
                using var command = Key(connection, transaction, id, closure.WriterId, closure.JobName);
                command.CommandText = "INSERT INTO main.journal_writer_job_closures VALUES($journal,$writer,$job,$path,$digest,$size) ON CONFLICT DO NOTHING;";
                command.Parameters.AddWithValue("$path", reference.RelativePath); command.Parameters.AddWithValue("$digest", reference.Sha256);
                command.Parameters.AddWithValue("$size", reference.SizeBytes); command.ExecuteNonQuery();
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.journal_writer_job_closures WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job " +
                    "AND plan_path=$path AND plan_digest=$digest AND plan_size=$size);";
                if ((long)command.ExecuteScalar()! != 1) throw new IOException("writer_job_closure_conflict");
            }
            return true;
        });
        return reference;
    }

    internal FrozenJobClosure? Read(Guid journalId, Guid writerId, string jobName, int sessionId)
        => ReadCore(journalId, writerId, jobName, sessionId, executionEvidence: false);

    internal FrozenJobClosure? ReadForExecutionEvidence(Guid journalId, Guid writerId, string jobName, int sessionId,
        BlobReference? expectedReference = null)
        => ReadCore(journalId, writerId, jobName, sessionId, executionEvidence: true, expectedReference: expectedReference);

    private FrozenJobClosure? ReadCore(Guid journalId, Guid writerId, string jobName, int sessionId, bool executionEvidence,
        AttemptWriterState? verifiedParent = null, BlobReference? expectedReference = null)
    {
        _ = verifiedParent?.RequireValidatedRead();
        FrozenJobClosure? closure = null;
        if (!executionEvidence && verifiedParent is null)
        {
            BlobReference reference;
            using (var stored = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime))
            using (var command = Key(stored, null, journalId, writerId, jobName))
            {
                command.CommandText = "SELECT plan_path,plan_digest,plan_size FROM main.journal_writer_job_closures WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job;";
                using var row = command.ExecuteReader();
                if (!row.Read()) return null;
                reference = new(row.GetString(0), row.GetInt64(2), row.GetString(1));
            }
            if (expectedReference is not null && reference != expectedReference) throw new IOException("writer_job_closure_reference_unbound");
            closure = new JobClosureCodec(instance, contracts, new(instance, contracts)).Read(reference);
            verifiedParent = ReadAttemptBinding(closure); expectedReference = reference;
        }
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration);
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = ReadInside(connection, transaction, journalId, writerId, jobName, sessionId, executionEvidence, verifiedParent, expectedReference, closure);
        transaction.Commit(); return result;
    }

    private FrozenJobClosure? ReadInside(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, Guid writerId,
        string jobName, int sessionId, bool executionEvidence, AttemptWriterState? verifiedParent = null,
        BlobReference? expectedReference = null, FrozenJobClosure? prepared = null)
    {
        using var command = Key(connection, transaction, journalId, writerId, jobName);
        command.CommandText = "SELECT plan_path,plan_digest,plan_size FROM main.journal_writer_job_closures WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job;";
        BlobReference reference;
        using (var row = command.ExecuteReader())
        {
            if (!row.Read()) return null;
            reference = new(row.GetString(0), row.GetInt64(2), row.GetString(1));
        }
        if (expectedReference is not null && reference != expectedReference) throw new IOException("writer_job_closure_reference_unbound");
        var result = prepared ?? new JobClosureCodec(instance, contracts, new(instance, contracts)).Read(reference);
        verifiedParent?.RequireClosure(result);
        if (executionEvidence)
        {
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.operation_journals j JOIN main.operations o ON o.operation_id=j.operation_id " +
                "JOIN main.journal_effect_gates g USING(journal_id) WHERE j.journal_id=$journal AND j.operation_id=$operation " +
                "AND j.operation_kind='runAttempt' AND o.operation_kind='execution.start' AND o.initial_journal_id=j.journal_id " +
                "AND ($executed=0 OR g.state='entered'));";
            command.Parameters.AddWithValue("$operation", result.OperationId.ToString("D")); command.Parameters.AddWithValue("$executed", result.WasExecuted);
            if (result.JournalIds.Length != 1 || result.JournalIds[0] != journalId || result.RunElapsedMilliseconds is null ||
                (long)command.ExecuteScalar()! != 1) throw new IOException("execution_writer_closure_unbound");
            VerifyJobOwner(connection, transaction, result, journalId, writerId, jobName, sessionId);
        }
        else VerifyBinding(connection, transaction, result, journalId, writerId, jobName, sessionId, verifiedParent);
        return result;
    }

    internal void RequireReference(Guid journalId, FrozenJobClosure closure, BlobReference expected, AttemptWriterState? verifiedParent = null)
    {
        var actual = ReadCore(journalId, closure.WriterId, closure.JobName, closure.SessionId, executionEvidence: false,
            verifiedParent: verifiedParent, expectedReference: expected) ?? throw new IOException("writer_job_closure_missing");
        if (actual.OperationId != closure.OperationId) throw new IOException("writer_job_closure_reference_unbound");
    }

    internal BlobReference RequireStoredReference(Guid journalId, FrozenJobClosure closure)
    {
        BlobReference reference;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime))
        using (var command = Key(connection, null, journalId, closure.WriterId, closure.JobName))
        {
            command.CommandText = "SELECT plan_path,plan_digest,plan_size FROM main.journal_writer_job_closures " +
                "WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job;";
            using var row = command.ExecuteReader();
            if (!row.Read()) throw new IOException("writer_job_closure_missing");
            reference = new(row.GetString(0), row.GetInt64(2), row.GetString(1));
        }
        RequireReference(journalId, closure, reference);
        return reference;
    }

    internal static void VerifyInventory(RuntimeInstance instance, StateDatabase database, ContractReader contracts, int pageSize = 128)
        => VerifyInventory(instance, database, contracts, pageSize, configurationsOnly: false);

    internal static void VerifyConfigurations(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
        => VerifyInventory(instance, database, contracts, 128, configurationsOnly: true);

    private static void VerifyInventory(RuntimeInstance instance, StateDatabase database, ContractReader contracts, int pageSize, bool configurationsOnly)
    {
        if (pageSize is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(pageSize));
        using var parents = JournalReadScope.HoldParents(instance, contracts);
        var store = new JobClosureStore(instance, database, contracts);
        var after = (Journal: "", Writer: "", Job: "");
        while (true)
        {
            var keys = new List<(Guid Journal, Guid Writer, string Job, int Session)>();
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime))
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT c.journal_id,c.writer_id,c.job_name,j.session_id FROM main.journal_writer_job_closures c LEFT JOIN main.journal_writer_jobs j " +
                    "ON j.journal_id=c.journal_id AND j.writer_id=c.writer_id AND j.job_name=c.job_name " +
                    "WHERE (c.journal_id,c.writer_id,c.job_name)>($journal,$writer,$job) " +
                    (configurationsOnly ? "AND c.journal_id NOT IN (SELECT journal_id FROM main.operation_journals WHERE operation_kind='runAttempt') " : "") +
                    "ORDER BY c.journal_id,c.writer_id,c.job_name LIMIT $limit;";
                command.Parameters.AddWithValue("$journal", after.Journal); command.Parameters.AddWithValue("$writer", after.Writer);
                command.Parameters.AddWithValue("$job", after.Job); command.Parameters.AddWithValue("$limit", pageSize);
                using (var row = command.ExecuteReader()) while (row.Read())
                {
                    if (row.IsDBNull(3)) throw new IOException("writer_job_closure_unbound");
                    keys.Add((Guid.Parse(row.GetString(0)), Guid.Parse(row.GetString(1)), row.GetString(2), row.GetInt32(3)));
                }
                transaction.Commit();
            }
            foreach (var key in keys) _ = store.ReadCore(key.Journal, key.Writer, key.Job, key.Session,
                executionEvidence: false) ?? throw new IOException("writer_job_closure_missing");
            if (keys.Count < pageSize) break;
            var last = keys[^1]; after = (last.Journal.ToString("D"), last.Writer.ToString("D"), last.Job);
        }
    }

    internal void VerifyAttempt(AttemptWriterState state)
    {
        _ = state.RequireValidatedRead();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT c.writer_id,c.job_name,j.session_id FROM main.journal_writer_job_closures c LEFT JOIN main.journal_writer_jobs j " +
            "ON j.journal_id=c.journal_id AND j.writer_id=c.writer_id AND j.job_name=c.job_name " +
            "WHERE c.journal_id=$journal ORDER BY c.writer_id,c.job_name LIMIT 1025;";
        command.Parameters.AddWithValue("$journal", state.Journal.JournalId.ToString("D"));
        var keys = new List<(Guid Writer, string Job, int Session)>();
        using (var row = command.ExecuteReader()) while (row.Read())
        {
            if (row.IsDBNull(2)) throw new IOException("writer_job_closure_unbound");
            keys.Add((Guid.Parse(row.GetString(0)), row.GetString(1), row.GetInt32(2)));
        }
        if (keys.Count > 1024) throw new IOException("execution_owned_job_capacity_exceeded");
        foreach (var key in keys)
            _ = ReadInside(connection, transaction, state.Journal.JournalId, key.Writer, key.Job, key.Session, executionEvidence: false, verifiedParent: state)
                ?? throw new IOException("writer_job_closure_missing");
        transaction.Commit();
    }

    private void VerifyBinding(SqliteConnection connection, SqliteTransaction transaction, FrozenJobClosure value,
        Guid journalId, Guid writerId, string jobName, int sessionId, AttemptWriterState? attempt)
    {
        using var command = Key(connection, transaction, journalId, writerId, jobName);
        command.CommandText = "SELECT operation_kind FROM main.operations WHERE operation_id=$operation;";
        command.Parameters.AddWithValue("$operation", value.OperationId.ToString("D"));
        var kind = command.ExecuteScalar() as string;
        if (kind == "execution.start")
        {
            VerifyAttemptBinding(connection, transaction, value, journalId, writerId, jobName, sessionId,
                attempt ?? throw new IOException("execution_writer_closure_unbound"));
            return;
        }
        var run = kind switch { "configuration.edit.begin" => false, "configuration.run.begin" => true,
            _ => throw new IOException("writer_job_closure_gate_unbound") };
        if (run != value.RunElapsedMilliseconds.HasValue) throw new IOException("writer_job_closure_elapsed_unbound");
        if (run) command.Parameters.AddWithValue("$elapsed", value.RunElapsedMilliseconds!.Value);
        var grouped = run && VerifyGroupClosure(connection, transaction, value, attempt);
        command.CommandText = run ? grouped
            ? "SELECT EXISTS(SELECT 1 FROM configuration.configuration_run_sessions WHERE session_id=$operation);"
            : "SELECT EXISTS(SELECT 1 FROM configuration.configuration_run_sessions WHERE session_id=$operation AND writer_gate=1 AND closed_elapsed_milliseconds=$elapsed);" :
            "SELECT EXISTS(SELECT 1 FROM configuration.configuration_edit_sessions WHERE session_id=$operation AND writer_gate=1);";
        if ((long)command.ExecuteScalar()! != 1) throw new IOException("writer_job_closure_gate_unbound");
        command.CommandText = run ? "SELECT journal_id,operation_id FROM configuration.configuration_run_resources WHERE session_id=$operation ORDER BY journal_id;" :
            "SELECT journal_id,$operation FROM configuration.configuration_edit_resources WHERE session_id=$operation ORDER BY journal_id;";
        var members = new Dictionary<Guid, Guid>();
        using (var rows = command.ExecuteReader()) while (rows.Read()) members.Add(Guid.Parse(rows.GetString(0)), Guid.Parse(rows.GetString(1)));
        if (!members.TryGetValue(journalId, out var childOperation) || !members.Keys.OrderBy(id => id.ToString("D"), StringComparer.Ordinal).SequenceEqual(value.JournalIds))
            throw new IOException("writer_job_closure_membership_unbound");
        command.CommandText = "SELECT j.operation_id,w.process_id,w.created_file_time,w.image_path,w.image_sha256,w.user_sid,w.elevated,b.session_id " +
            "FROM main.journal_writer_jobs b JOIN main.operation_journals j ON j.journal_id=b.journal_id JOIN main.journal_writers w ON w.journal_id=b.journal_id AND w.writer_id=b.writer_id " +
            "WHERE b.journal_id=$journal AND b.writer_id=$writer AND b.job_name=$job" +
            (run && !grouped ? " AND (SELECT count(*) FROM main.journal_writer_jobs WHERE journal_id=$journal)=1;" : ";");
        using (var row = command.ExecuteReader())
            if (!row.Read() || row.GetString(0) != childOperation.ToString("D") || value.WriterId != writerId || value.JobName != jobName ||
                value.SessionId != sessionId || row.GetInt32(7) != sessionId || !value.JournalIds.Contains(journalId) || ReadProcess(row, 1) != value.Creator)
                throw new IOException("writer_job_closure_owner_unbound");
        command.CommandText = "SELECT process_id,created_file_time,image_path,image_sha256,user_sid,elevated FROM main.journal_writer_job_roots " +
            "WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job;";
        using var root = command.ExecuteReader();
        var exists = root.Read();
        if (value.WasExecuted != exists || exists && ReadProcess(root, 0) != value.Root) throw new IOException("writer_job_closure_root_unbound");
    }

    internal AttemptWriterState? ReadAttemptBinding(FrozenJobClosure value)
    {
        string? kind;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT operation_kind FROM main.operations WHERE operation_id=$operation;";
            command.Parameters.AddWithValue("$operation", value.OperationId.ToString("D"));
            kind = command.ExecuteScalar() as string;
        }
        if (kind == "execution.start") return AttemptWriterState.BindClosure(instance, database, contracts, value);
        return null;
    }

    internal void RequireAttemptStart(AttemptWriterState state, StepWriterState step)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration);
        using var transaction = connection.BeginTransaction(deferred: true);
        RequireAttemptOpen(connection, transaction, state, step);
        transaction.Commit();
    }

    internal void RecordAttemptRoot(AttemptWriterState state, StepWriterState step, ProcessIdentity root)
    {
        var input = state.Total.Steps.Single(item => item.StepId == step.StepId);
        var (image, digest) = ExecutionBindingRoles.Image(input, step.Role);
        if (!StringComparer.OrdinalIgnoreCase.Equals(root.ImagePath, image) || root.ImageSha256 != digest)
            throw new IOException("execution_writer_root_unbound");
        database.Write((connection, transaction) =>
        {
            RequireAttemptOpen(connection, transaction, state, step);
            using var command = Key(connection, transaction, state.Journal.JournalId, step.WriterId, step.JobName);
            command.CommandText = "INSERT INTO main.journal_writer_job_roots VALUES($journal,$writer,$job,$pid,$created,$path,$hash,$sid,$elevated);";
            ProcessParameters(command, root); command.ExecuteNonQuery(); return true;
        });
    }

    private static void RequireAttemptOpen(SqliteConnection connection, SqliteTransaction transaction, AttemptWriterState state, StepWriterState step)
    {
        if (state.Journal.Phase != JournalPhase.Applying || step.ClosedElapsedMilliseconds is not null ||
            step.WriterId != JournalWriterStore.CurrentWriter.WriterId) throw new IOException("execution_writer_start_closed");
        AttemptWriterState.RequireCursor(connection, transaction, state.Journal, EffectGateState.Entered);
        ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, state.Journal.JournalId);
        PcStageProjection.RequireMainStartInside(connection, transaction, state, step);
        ProviderLauncherProjection.RequireMainInside(connection, transaction, state, step);
        AndroidControlProjection.RequireMainStartInside(connection, transaction, state, step);
        ExecutionStepAdmission.RequireOpenRoles(state.Total, state.Jobs, step.StepId, step.Role, state.Latest.Root);
        var ordinal = state.Total.Steps.ToList().FindIndex(item => item.StepId == step.StepId);
        if (ordinal < 0 || BindingSkipCodec.Processed(state.Latest.Root, state.Total, state.Jobs).Length != ordinal + 1)
            throw new IOException("execution_writer_start_closed");
        ExecutionStepAdmission.RequirePredecessorsInside(connection, transaction, state, state.Latest, ordinal);
        using var command = Key(connection, transaction, state.Journal.JournalId, step.WriterId, step.JobName);
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.journal_writer_jobs WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job AND session_id=$session) " +
            "AND NOT EXISTS(SELECT 1 FROM main.journal_writer_job_roots WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job) " +
            "AND NOT EXISTS(SELECT 1 FROM main.journal_writer_job_closures WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job);";
        command.Parameters.AddWithValue("$session", step.SessionId);
        if ((long)command.ExecuteScalar()! != 1) throw new IOException("execution_writer_start_closed");
    }

    private static void VerifyAttemptBinding(SqliteConnection connection, SqliteTransaction transaction, FrozenJobClosure value,
        Guid journalId, Guid writerId, string jobName, int sessionId, AttemptWriterState state)
    {
        if (value.OperationId != state.Journal.OperationId || value.JournalIds.Length != 1 || value.JournalIds[0] != journalId ||
            journalId != state.Journal.JournalId || value.WriterId != writerId || value.JobName != jobName || value.SessionId != sessionId)
            throw new IOException("execution_writer_closure_unbound");
        AttemptWriterState.RequireCursor(connection, transaction, state.Journal, state.Gate.State);
        VerifyJobOwner(connection, transaction, value, journalId, writerId, jobName, sessionId);
    }

    private static void VerifyJobOwner(SqliteConnection connection, SqliteTransaction transaction, FrozenJobClosure value,
        Guid journalId, Guid writerId, string jobName, int sessionId)
    {
        if (value.WriterId != writerId || value.JobName != jobName || value.SessionId != sessionId)
            throw new IOException("execution_writer_closure_unbound");
        using var command = Key(connection, transaction, journalId, writerId, jobName);
        command.CommandText = "SELECT w.process_id,w.created_file_time,w.image_path,w.image_sha256,w.user_sid,w.elevated,b.session_id FROM main.journal_writer_jobs b " +
            "JOIN main.journal_writers w ON w.journal_id=b.journal_id AND w.writer_id=b.writer_id WHERE b.journal_id=$journal AND b.writer_id=$writer AND b.job_name=$job;";
        using (var row = command.ExecuteReader())
            if (!row.Read() || ReadProcess(row, 0) != value.Creator || row.GetInt32(6) != sessionId) throw new IOException("execution_writer_closure_owner_unbound");
        command.CommandText = "SELECT process_id,created_file_time,image_path,image_sha256,user_sid,elevated FROM main.journal_writer_job_roots WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job;";
        using var root = command.ExecuteReader(); var exists = root.Read();
        if (exists != value.WasExecuted || exists && ReadProcess(root, 0) != value.Root) throw new IOException("execution_writer_closure_root_unbound");
    }

    private static SqliteCommand Key(SqliteConnection connection, SqliteTransaction? transaction, Guid journal, Guid writer, string job)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$journal", journal.ToString("D")); command.Parameters.AddWithValue("$writer", writer.ToString("D"));
        command.Parameters.AddWithValue("$job", job); return command;
    }
    private static void ProcessParameters(SqliteCommand command, ProcessIdentity value)
    {
        command.Parameters.AddWithValue("$pid", value.ProcessId); command.Parameters.AddWithValue("$created", value.CreatedAtUtc.ToFileTimeUtc());
        command.Parameters.AddWithValue("$path", value.ImagePath); command.Parameters.AddWithValue("$hash", value.ImageSha256);
        command.Parameters.AddWithValue("$sid", value.UserSid); command.Parameters.AddWithValue("$elevated", value.IsElevated);
    }
    private static ProcessIdentity ReadProcess(SqliteDataReader row, int offset) => new(row.GetInt32(offset), DateTime.FromFileTimeUtc(row.GetInt64(offset + 1)),
        row.GetString(offset + 2), row.GetString(offset + 3), row.GetString(offset + 4), row.GetBoolean(offset + 5));
    private bool VerifyGroupClosure(SqliteConnection connection, SqliteTransaction transaction,
        FrozenJobClosure value, AttemptWriterState? state)
    {
        RegisteredJournal parent;
        ExecutionPlan total;
        JournalDocument latest;
        if (state is not null) { parent = state.Journal; total = state.Total; latest = state.Latest; }
        else
        {
            using var session = connection.CreateCommand(); session.Transaction = transaction;
            session.CommandText = "SELECT parent_journal_id,step_id FROM configuration.configuration_run_sessions WHERE session_id=$session;";
            session.Parameters.AddWithValue("$session", value.OperationId.ToString("D"));
            Guid parentId; string stepId;
            using (var row = session.ExecuteReader())
            {
                if (!row.Read()) throw new IOException("configuration_group_parent_unbound");
                parentId = Guid.Parse(row.GetString(0)); stepId = row.GetString(1);
            }
            parent = RecoveryJournalRegistry.ReadInside(connection, transaction, parentId);
            var inputs = AttemptPreparedBinding.ReadPlanGraph(instance, contracts, parent, database);
            total = inputs.Total;
            if (total.Steps.Single(item => item.StepId == stepId).Hooks is not { Count: > 0 }) return false;
            var history = new JournalStore(instance, contracts).ReadAllRevisions(parentId);
            var projected = history.SingleOrDefault(item => item.Revision == parent.Revision);
            if (projected is null || projected.PlanPath != parent.PlanPath || projected.PlanDigest != parent.PlanDigest ||
                projected.Phase != parent.Phase) throw new IOException("configuration_group_parent_unbound");
            latest = history[^1];
        }
        var job = WriterIntentCodec.ReadAllJobs(latest.Root, total).SingleOrDefault(job => job.JobName == value.JobName);
        if (job is null || job.Role == "judgement" || total.Steps.Single(step => step.StepId == job.StepId).Hooks is not { Count: > 0 } ||
            job.WriterId != value.WriterId || job.SessionId != value.SessionId || job.ClosedElapsedMilliseconds != value.RunElapsedMilliseconds)
            throw new IOException("configuration_group_closure_unbound");
        // Stop ownership is read directly so output verification cannot re-enter the parent attempt verifier.
        var gate = new JournalEffectGate(database).ReadInside(connection, transaction, parent.JournalId);
        if (gate.InstanceId != instance.InstanceId || gate.OperationId != parent.OperationId || gate.PlanDigest != parent.PlanDigest ||
            gate.Kind != JournalKind.RunAttempt) throw new IOException("configuration_group_parent_unbound");
        AttemptWriterState.RequireCursor(connection, transaction, parent, gate.State);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM configuration.configuration_run_sessions WHERE session_id=$session AND owner_operation_id=$owner " +
            "AND parent_journal_id=$parent AND step_id=$step AND invocation_ordinal=$invocation AND ($primary=0 OR primary_job_name=$job));";
        command.Parameters.AddWithValue("$invocation", ExecutionInvocationCodec.Ordinal(
            ExecutionInvocationCodec.ForJob(latest.Root, total, job.JobName), job.StepId));
        command.Parameters.AddWithValue("$primary", job.Role == "process"); command.Parameters.AddWithValue("$job", job.JobName);
        command.Parameters.AddWithValue("$session", value.OperationId.ToString("D")); command.Parameters.AddWithValue("$owner", parent.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$parent", parent.JournalId.ToString("D")); command.Parameters.AddWithValue("$step", job.StepId);
        if ((long)command.ExecuteScalar()! != 1) throw new IOException("configuration_group_closure_unbound");
        VerifyJobOwner(connection, transaction, value, parent.JournalId, value.WriterId, value.JobName, value.SessionId);
        return true;
    }
}
