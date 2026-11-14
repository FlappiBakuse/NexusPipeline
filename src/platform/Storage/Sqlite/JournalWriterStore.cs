using Microsoft.Data.Sqlite;
using System.Security.Principal;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Sqlite;

public sealed record JournalWriter(Guid WriterId, ProcessIdentity Process);
public sealed record WriterStopObservation(bool IsComplete, WriterState State);

public sealed class JournalWriterStore(StateDatabase database)
{
    private static readonly Lazy<JournalWriter> Current = new(() => OperatingSystem.IsWindows()
        ? new(Guid.NewGuid(), ProcessIdentityReader.Read(Environment.ProcessId)) : throw new PlatformNotSupportedException());
    internal static JournalWriter CurrentWriter => Current.Value;

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, JournalWriter writer)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO main.journal_writers VALUES($journal,$writer,$pid,$created,$path,$hash,$sid,$elevated);";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$writer", writer.WriterId.ToString("D"));
        command.Parameters.AddWithValue("$pid", writer.Process.ProcessId);
        command.Parameters.AddWithValue("$created", writer.Process.CreatedAtUtc.ToFileTimeUtc());
        command.Parameters.AddWithValue("$path", writer.Process.ImagePath);
        command.Parameters.AddWithValue("$hash", writer.Process.ImageSha256);
        command.Parameters.AddWithValue("$sid", writer.Process.UserSid);
        command.Parameters.AddWithValue("$elevated", writer.Process.IsElevated ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public void RegisterJob(Guid journalId, OwnedWorkerJob job) => RegisterJob([journalId], job);

    internal void RegisterJob(IReadOnlyList<Guid> journalIds, OwnedWorkerJob job, AttemptWriterState? execution = null, string? stepId = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var owner = CurrentWriter;
        var ids = journalIds.ToArray();
        if (ids.Length is < 1 or > 32 || ids.Distinct().Count() != ids.Length) throw new IOException("writer_job_registration_invalid");
        if (job.GetAccounting().TotalProcesses != 0) throw new IOException("writer_job_must_be_registered_before_execution");
        var jobName = job.Name;
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        var session = current.SessionId;
        var grouped = execution is not null && execution.Total.Steps.Single(step => step.StepId == stepId).Hooks is { Count: > 0 };
        var isPrimary = execution is null || execution.Jobs.Single(item => item.JobName == jobName).Role == "process";
        database.Write((connection, transaction) =>
        {
            if (execution is not null)
            {
                AttemptWriterState.RequireCursor(connection, transaction, execution.Journal, execution.Gate.State);
                ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, execution.Journal.JournalId);
                using (var members = connection.CreateCommand())
                {
                    members.Transaction = transaction;
                    members.CommandText = "SELECT r.journal_id FROM configuration.configuration_run_resources r JOIN configuration.configuration_run_sessions s USING(session_id) " +
                        "WHERE s.parent_journal_id=$parent AND s.owner_operation_id=$owner AND s.step_id=$step AND s.invocation_ordinal=$invocation ORDER BY r.journal_id LIMIT 33;";
                    members.Parameters.AddWithValue("$parent", execution.Journal.JournalId.ToString("D"));
                    members.Parameters.AddWithValue("$owner", execution.Journal.OperationId.ToString("D")); members.Parameters.AddWithValue("$step", stepId);
                    members.Parameters.AddWithValue("$invocation", ExecutionInvocationCodec.Ordinal(execution.Latest.Root, stepId!));
                    using var rows = members.ExecuteReader(); var expectedIds = new HashSet<Guid>();
                    while (rows.Read()) expectedIds.Add(Guid.Parse(rows.GetString(0)));
                    if (!expectedIds.SetEquals(ids)) throw new IOException("configuration_group_membership_unbound");
                }
                var step = execution.Jobs.SingleOrDefault(item => item.JobName == jobName);
                if (step is null || step.StepId != stepId || step.JobName != jobName || step.ClosedElapsedMilliseconds is not null)
                    throw new IOException("execution_writer_job_unbound");
                if (step.Role == "judgement") throw new IOException("configuration_group_role_unbound");
                ExecutionStepAdmission.RequirePredecessorsInside(connection, transaction, execution, execution.Latest,
                    execution.Total.Steps.ToList().FindIndex(item => item.StepId == stepId));
            }
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO main.journal_writer_jobs SELECT $journal,$writer,$job,$session WHERE EXISTS " +
                "(SELECT 1 FROM main.operation_journals WHERE journal_id=$journal AND (phase='prepared' OR $grouped=1 AND phase='applied')) AND NOT EXISTS " +
                "(SELECT 1 FROM configuration.configuration_edit_sessions s JOIN main.operation_journals j ON j.operation_id=s.session_id WHERE j.journal_id=$journal AND s.writer_gate=1) AND NOT EXISTS " +
                "(SELECT 1 FROM configuration.configuration_edit_sessions s JOIN main.operation_journals j ON j.operation_id=s.session_id JOIN main.journal_writer_jobs b ON b.journal_id=j.journal_id WHERE j.journal_id=$journal) AND NOT EXISTS " +
                "(SELECT 1 FROM configuration.configuration_run_resources r JOIN configuration.configuration_run_sessions s ON s.session_id=r.session_id WHERE r.journal_id=$journal AND s.writer_gate=1) AND NOT EXISTS " +
                "(SELECT 1 FROM configuration.configuration_run_resources r JOIN main.journal_writer_jobs b ON b.journal_id=r.journal_id WHERE r.journal_id=$journal AND $grouped=0) AND NOT EXISTS " +
                "(SELECT 1 FROM main.operation_journals j JOIN main.operations o ON o.operation_id=j.operation_id WHERE j.journal_id=$journal AND o.operation_kind='execution.start');";
            var journal = command.Parameters.Add("$journal", SqliteType.Text);
            command.Parameters.AddWithValue("$writer", owner.WriterId.ToString("D"));
            command.Parameters.AddWithValue("$job", jobName);
            command.Parameters.AddWithValue("$session", session);
            command.Parameters.AddWithValue("$grouped", grouped);
            foreach (var id in ids)
            {
                journal.Value = id.ToString("D");
                if (command.ExecuteNonQuery() != 1) throw new IOException("writer_job_registration_unbound");
                if (isPrimary)
                {
                    using var primary = connection.CreateCommand(); primary.Transaction = transaction;
                    primary.CommandText = "UPDATE configuration.configuration_run_sessions SET primary_job_name=$job WHERE session_id IN " +
                        "(SELECT session_id FROM configuration.configuration_run_resources WHERE journal_id=$journal) AND writer_gate=0 " +
                        "AND (primary_job_name IS NULL OR primary_job_name=$job);";
                    primary.Parameters.AddWithValue("$job", jobName); primary.Parameters.AddWithValue("$journal", id.ToString("D"));
                    primary.ExecuteNonQuery();
                    primary.CommandText = "SELECT NOT EXISTS(SELECT 1 FROM configuration.configuration_run_resources r JOIN configuration.configuration_run_sessions s USING(session_id) " +
                        "WHERE r.journal_id=$journal AND (s.primary_job_name IS NULL OR s.primary_job_name<>$job));";
                    if ((long)primary.ExecuteScalar()! != 1) throw new IOException("configuration_primary_job_unbound");
                }
            }
            return true;
        });
    }

    public WriterStopObservation Observe(Guid journalId) => Observe(journalId, null);

    internal Guid RequireCurrentExecutor(Guid journalId, ProcessIdentityLease? executor = null) =>
        RequireCurrentWriter(journalId, requireNoWorkers: true, executor);
    internal Guid RequireRegisteredExecutor(Guid journalId, ProcessIdentityLease? executor = null) =>
        RequireCurrentWriter(journalId, requireNoWorkers: false, executor);

    internal static void RegisterAttemptJobInside(SqliteConnection connection, SqliteTransaction transaction,
        RegisteredJournal expected, StepWriterState step, AttemptWriterState? context = null, JournalDocument? before = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, expected.JournalId);
        if (context is null || before is null || context.Journal.JournalId != expected.JournalId || before.Revision != expected.Revision || before.Phase != expected.Phase)
            throw new IOException("execution_writer_registration_unbound");
        var ordinal = ExecutionStepAdmission.RequireNext(before, context.Total, step.StepId, step.Role);
        ExecutionStepAdmission.RequirePredecessorsInside(connection, transaction, context, before, ordinal);
        var owner = CurrentWriter;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        if (owner.WriterId != step.WriterId || process.SessionId != step.SessionId || ProcessIdentityReader.Read(Environment.ProcessId) != owner.Process)
            throw new IOException("execution_writer_executor_unbound");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO main.journal_writer_jobs SELECT $journal,$writer,$job,$session WHERE EXISTS " +
            "(SELECT 1 FROM main.operation_journals j JOIN main.operations o ON o.operation_id=j.operation_id JOIN main.journal_effect_gates g ON g.journal_id=j.journal_id " +
            "WHERE j.journal_id=$journal AND j.operation_id=$operation AND j.operation_kind='runAttempt' AND j.phase=$phase " +
            "AND j.revision=$revision AND o.operation_kind='execution.start' AND o.initial_journal_id=j.journal_id AND g.state=$gate) " +
            "AND EXISTS(SELECT 1 FROM main.journal_writers WHERE journal_id=$journal AND writer_id=$writer) " +
            "AND NOT EXISTS(SELECT 1 FROM main.journal_writer_jobs WHERE journal_id=$journal AND job_name=$job);";
        command.Parameters.AddWithValue("$journal", expected.JournalId.ToString("D")); command.Parameters.AddWithValue("$operation", expected.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$revision", expected.Revision); command.Parameters.AddWithValue("$writer", step.WriterId.ToString("D"));
        command.Parameters.AddWithValue("$phase", expected.Phase.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$gate", expected.Phase == JournalPhase.Prepared ? "unentered" : "entered");
        command.Parameters.AddWithValue("$job", step.JobName); command.Parameters.AddWithValue("$session", step.SessionId);
        if (command.ExecuteNonQuery() != 1) throw new IOException("execution_writer_job_registration_unbound");
    }

    internal WriterStopObservation ObserveForCurrentExecutor(Guid journalId, ProcessIdentityLease? identity = null)
    {
        using var timing = StartupProfile.Measure("writer.executor");
        return Observe(journalId, null, RequireExecutor(identity));
    }

    internal WriterStopObservation ObserveWithExecutor(SqliteConnection connection, SqliteTransaction transaction,
        Guid journalId, ProcessIdentityLease? identity)
    {
        using var timing = StartupProfile.Measure("writer.executor");
        return ObserveInside(connection, transaction, journalId, null, RequireExecutor(identity));
    }

    private ProcessIdentity RequireExecutor(ProcessIdentityLease? identity)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var current = CurrentWriter;
        if (((identity ?? database.ExecutorIdentity)?.ReadCurrent() ?? ProcessIdentityReader.Read(Environment.ProcessId)) != current.Process)
            throw new IOException("journal_executor_identity_changed");
        return current.Process;
    }

    private Guid RequireCurrentWriter(Guid journalId, bool requireNoWorkers, ProcessIdentityLease? executor = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var writer = CurrentWriter;
        if (((executor ?? database.ExecutorIdentity)?.ReadCurrent() ?? ProcessIdentityReader.Read(Environment.ProcessId)) != writer.Process)
            throw new IOException("cleanup_executor_identity_changed");
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.journal_writers WHERE journal_id=$journal AND writer_id=$writer " +
            "AND process_id=$pid AND created_file_time=$created AND image_path=$path AND image_sha256=$hash AND user_sid=$sid AND elevated=$elevated) " +
            "AND ($noWorkers=0 OR NOT EXISTS(SELECT 1 FROM main.journal_writer_jobs WHERE journal_id=$journal AND writer_id=$writer));";
        command.Parameters.AddWithValue("$noWorkers", requireNoWorkers);
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$writer", writer.WriterId.ToString("D"));
        command.Parameters.AddWithValue("$pid", writer.Process.ProcessId);
        command.Parameters.AddWithValue("$created", writer.Process.CreatedAtUtc.ToFileTimeUtc());
        command.Parameters.AddWithValue("$path", writer.Process.ImagePath);
        command.Parameters.AddWithValue("$hash", writer.Process.ImageSha256);
        command.Parameters.AddWithValue("$sid", writer.Process.UserSid);
        command.Parameters.AddWithValue("$elevated", writer.Process.IsElevated ? 1 : 0);
        if ((long)command.ExecuteScalar()! != 1) throw new IOException("cleanup_executor_not_registered_or_has_workers");
        return writer.WriterId;
    }

    internal bool IsRegistered(Guid journalId, Guid writerId)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.journal_writers WHERE journal_id=$journal AND writer_id=$writer);";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$writer", writerId.ToString("D"));
        return (long)command.ExecuteScalar()! == 1;
    }

    internal WriterStopObservation Observe(Guid journalId, Guid? executingRecovery) => Observe(journalId, executingRecovery, null);

    internal WriterStopObservation ObserveCreators(Guid journalId, Guid? executingRecovery) =>
        Observe(journalId, executingRecovery, null, creatorsOnly: true);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal bool AreOthersStopped(Maintenance.MaintenanceRegisteredActor actualActor, InstanceLease lease,
        Guid journalId)
    {
        actualActor.RequireCurrent(lease);
        var others = Observe(journalId, null, null, actualActor);
        actualActor.RequireCurrent(lease);
        return others is { IsComplete: true, State: WriterState.Stopped };
    }

    private WriterStopObservation Observe(Guid journalId, Guid? executingRecovery, ProcessIdentity? executingProcess,
        Maintenance.MaintenanceRegisteredActor? maintenanceActor = null, bool creatorsOnly = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = ObserveInside(connection, transaction, journalId, executingRecovery, executingProcess, maintenanceActor, creatorsOnly);
        transaction.Commit();
        return result;
    }

    private WriterStopObservation ObserveInside(SqliteConnection connection, SqliteTransaction transaction,
        Guid journalId, Guid? executingRecovery, ProcessIdentity? executingProcess,
        Maintenance.MaintenanceRegisteredActor? maintenanceActor = null, bool creatorsOnly = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        maintenanceActor?.RequireRegisteredInside(connection, transaction, journalId);
        command.CommandText = "SELECT writer_id,process_id,created_file_time,image_path,image_sha256,user_sid,elevated FROM main.journal_writers WHERE journal_id=$journal LIMIT 1025;";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        var writers = new List<JournalWriter>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) writers.Add(new(Guid.Parse(reader.GetString(0)), new(reader.GetInt32(1), DateTime.FromFileTimeUtc(reader.GetInt64(2)),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6) == 1)));
        if (writers.Count is 0 or > 1024 || writers.Any(writer => !Recognized(writer))) return new(false, WriterState.Unknown);
        var state = WriterState.Stopped;
        foreach (var writer in writers)
        {
            if (maintenanceActor is not null && writer.WriterId == maintenanceActor.Writer.WriterId)
            {
                if (writer.Process != maintenanceActor.Writer.Process) return new(false, WriterState.Unknown);
                continue;
            }
            var observed = writer.WriterId == executingRecovery || writer.Process == executingProcess
                ? WriterState.Stopped : WriterStopEvidence.ObserveProcess(writer.Process);
            if (observed == WriterState.Unknown) return new(false, WriterState.Unknown);
            // Other live owners retain their kill-on-close lifetime; observation must not extend it.
            if (observed == WriterState.Running) { state = WriterState.Running; continue; }
            if (creatorsOnly) continue;
            command.CommandText = $"SELECT job_name,session_id FROM main.journal_writer_jobs WHERE journal_id=$journal AND writer_id=$writer LIMIT {JournalCapacityLimits.MaximumWriterJobs + 1};";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            command.Parameters.AddWithValue("$writer", writer.WriterId.ToString("D"));
            using var jobs = command.ExecuteReader();
            var count = 0;
            while (jobs.Read())
            {
                if (++count > JournalCapacityLimits.MaximumWriterJobs) return new(false, WriterState.Unknown);
                var jobName = jobs.GetString(0); var sessionId = jobs.GetInt32(1);
                var job = WriterStopEvidence.ObserveJob(jobName, sessionId);
                if (job == WriterState.Running) state = WriterState.Running;
                else
                {
                    var instance = new InstanceRoot(new()).OpenExisting(database.RuntimeRoot);
                    var closure = new JobClosureStore(instance, database, new()).Read(journalId, writer.WriterId, jobName, sessionId);
                    if (job == WriterState.Unknown && closure is null) return new(false, WriterState.Unknown);
                    if (closure is not null)
                        foreach (var member in closure.Family)
                            if (WriterStopEvidence.ObserveProcess(member) != WriterState.Stopped) return new(false, WriterState.Unknown);
                }
            }
        }
        return new(true, state);
    }

    private static bool Recognized(JournalWriter writer)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (writer.WriterId == Guid.Empty || !Path.IsPathFullyQualified(writer.Process.ImagePath) ||
            writer.Process.ImageSha256.Length != 64 ||
            writer.Process.ImageSha256.Any(value => value is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) return false;
        try
        {
            return Path.GetFullPath(writer.Process.ImagePath) == writer.Process.ImagePath &&
                new SecurityIdentifier(writer.Process.UserSid).Value == writer.Process.UserSid;
        }
        catch (ArgumentException) { return false; }
    }
}
