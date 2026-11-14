using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

[SupportedOSPlatform("windows")]
internal sealed class MaintenanceRegisteredActor : IDisposable
{
    private readonly MaintenanceBootstrapPayload _bootstrap;
    private SafeKernelHandle? _jobLifetime;
    private int _restoredStartClaimed;
    internal JournalWriter Writer { get; }
    internal string JobName { get; }

    private MaintenanceRegisteredActor(MaintenanceBootstrapPayload bootstrap, JournalWriter writer,
        string jobName, SafeKernelHandle jobLifetime)
    { _bootstrap = bootstrap; Writer = writer; JobName = jobName; _jobLifetime = jobLifetime; }

    internal static MaintenanceRegisteredActor BindCurrent(MaintenanceBootstrapPayload bootstrap, Guid writerId, string jobName)
    {
        if (writerId == Guid.Empty || !RecognizedJobName(jobName)) throw Invalid();
        var current = ProcessIdentityReader.Read(Environment.ProcessId); bootstrap.RequireHelper(current);
        var writer = new JournalWriter(writerId, current);
        using (var connection = DatabaseConnections.Open(bootstrap.Inputs.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System))
        using (var transaction = connection.BeginTransaction(deferred: true))
        { RequireRegistered(connection, transaction, bootstrap.Inputs.Plan.JournalId, writer, jobName); transaction.Commit(); }
        var job = JobNative.OpenJobObjectW(4, false, jobName);
        try
        {
            using var process = JobNative.OpenProcess(0x101000, false, current.ProcessId);
            if (job.IsInvalid || process.IsInvalid || !JobNative.IsProcessInJob(process, job, out var member) || !member ||
                JobStateReader.Limits(job).Basic.Flags != 0x2800) throw Invalid();
            var result = new MaintenanceRegisteredActor(bootstrap, writer, jobName, job);
            result.RequireBound(); return result;
        }
        catch { job.Dispose(); throw; }
    }

    internal static Guid RegisterSuspended(PreparedUpdateHandoff handoff, InstanceLease lease,
        MaintenanceBootstrapPayload bootstrap, MaintenanceStartupJob actualJob, OwnedWorkerIdentity actualRoot)
    {
        lease.VerifyHeld(bootstrap.Inputs.Instance); bootstrap.RequireCurrent(); actualJob.RequirePersistingRoot(actualRoot);
        var process = new ProcessIdentity(actualRoot.ProcessId, actualRoot.CreatedAtUtc, actualRoot.ImagePath,
            actualRoot.ImageSha256, actualRoot.UserSid, actualRoot.IsElevated);
        bootstrap.RequireHelper(process);
        if (!handoff.Root.IsActive || handoff.HandoffId != bootstrap.Inputs.Operation.HandoffId ||
            handoff.PlanReference != bootstrap.Inputs.Operation.Plan || actualRoot.InstanceId != bootstrap.Inputs.Instance.InstanceId ||
            actualRoot.AttemptId != handoff.HandoffId) throw Invalid();
        var writer = new JournalWriter(Guid.NewGuid(), process);
        using var creator = Process.GetCurrentProcess();
        bootstrap.Inputs.Database.Write((connection, transaction) =>
        {
            actualJob.RequirePersistingRoot(actualRoot);
            bootstrap.Inputs.RequireInside(connection, transaction);
            var registered = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, handoff.Plan.JournalId);
            if (registered.Phase != JournalPhase.Prepared || registered.OperationId != handoff.Plan.OperationId ||
                registered.PlanDigest != handoff.PlanReference.Sha256 || registered.PlanPath != handoff.PlanReference.RelativePath) throw Invalid();
            using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = "SELECT COUNT(*)=1 AND COALESCE(SUM(writer_id=$creator),0)=1 FROM main.journal_writers WHERE journal_id=$journal;";
                existing.Parameters.AddWithValue("$creator", JournalWriterStore.CurrentWriter.WriterId.ToString("D"));
                existing.Parameters.AddWithValue("$journal", handoff.Plan.JournalId.ToString("D"));
                if ((long)existing.ExecuteScalar()! != 1) throw Invalid();
            }
            JournalWriterStore.Insert(connection, transaction, handoff.Plan.JournalId, writer);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO main.journal_writer_jobs VALUES($journal,$writer,$job,$session);";
            command.Parameters.AddWithValue("$journal", handoff.Plan.JournalId.ToString("D"));
            command.Parameters.AddWithValue("$writer", writer.WriterId.ToString("D")); command.Parameters.AddWithValue("$job", actualJob.Name);
            command.Parameters.AddWithValue("$session", creator.SessionId); command.ExecuteNonQuery();
            RecordRoot(connection, transaction, handoff.Plan.JournalId, writer, actualJob.Name);
            return true;
        });
        return writer.WriterId;
    }

    internal Guid RegisterCandidateSuspended(MaintenanceHandoffInputs inputs, InstanceLease lease,
        MaintenanceStartupJob actualJob, OwnedWorkerIdentity actualRoot)
    {
        RequireCurrent(lease); inputs.RequireCurrent(); actualJob.RequirePersistingRoot(actualRoot);
        var process = new ProcessIdentity(actualRoot.ProcessId, actualRoot.CreatedAtUtc, actualRoot.ImagePath,
            actualRoot.ImageSha256, actualRoot.UserSid, actualRoot.IsElevated);
        var expected = inputs.Manifest.Files.Single(file => file.RelativePath == inputs.Manifest.HostEntry);
        if (inputs.Plan.JournalId != _bootstrap.Inputs.Plan.JournalId || actualRoot.InstanceId != inputs.Instance.InstanceId ||
            actualRoot.AttemptId != inputs.Operation.HandoffId || process.UserSid != Writer.Process.UserSid ||
            process.IsElevated != Writer.Process.IsElevated || process.ImageSha256 != expected.Sha256 ||
            !StringComparer.OrdinalIgnoreCase.Equals(process.ImagePath,
                PhysicalPathPolicy.NormalizeSyntax(Path.Combine(inputs.Plan.TargetPath, expected.RelativePath)))) throw Invalid();
        var writer = new JournalWriter(Guid.NewGuid(), process);
        using var creator = Process.GetCurrentProcess();
        inputs.Database.Write((connection, transaction) =>
        {
            actualJob.RequirePersistingRoot(actualRoot); RequireRegisteredInside(connection, transaction, inputs.Plan.JournalId);
            var journal = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, inputs.Plan.JournalId);
            if (journal.Phase != JournalPhase.Applied) throw Invalid();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM main.journal_writers WHERE journal_id=$journal;";
            command.Parameters.AddWithValue("$journal", inputs.Plan.JournalId.ToString("D"));
            if ((long)command.ExecuteScalar()! != 2) throw Invalid();
            JournalWriterStore.Insert(connection, transaction, inputs.Plan.JournalId, writer);
            command.CommandText = "INSERT INTO main.journal_writer_jobs VALUES($journal,$writer,$job,$session);";
            command.Parameters.AddWithValue("$writer", writer.WriterId.ToString("D")); command.Parameters.AddWithValue("$job", actualJob.Name);
            command.Parameters.AddWithValue("$session", creator.SessionId); command.ExecuteNonQuery();
            RecordRoot(connection, transaction, inputs.Plan.JournalId, writer, actualJob.Name);
            return true;
        });
        return writer.WriterId;
    }

    internal Guid RegisterRestoredSuspended(MaintenanceStartupInputs inputs, StateDatabase database, InstanceLease lease,
        MaintenanceStartupJob actualJob, OwnedWorkerIdentity actualRoot)
    {
        lease.VerifyHeld(_bootstrap.Inputs.Instance); RequireRestoredBound(inputs);
        if (Interlocked.Exchange(ref _restoredStartClaimed, 1) != 0) throw Invalid();
        actualJob.RequirePersistingRoot(actualRoot);
        var process = new ProcessIdentity(actualRoot.ProcessId, actualRoot.CreatedAtUtc, actualRoot.ImagePath,
            actualRoot.ImageSha256, actualRoot.UserSid, actualRoot.IsElevated);
        var restored = inputs.Restored ?? throw Invalid();
        restored.RequireHost(process);
        if (database.InstanceId != _bootstrap.Inputs.Instance.InstanceId ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath, _bootstrap.Inputs.Database.DatabasePath)) throw Invalid();
        if (actualRoot.InstanceId != _bootstrap.Inputs.Instance.InstanceId || actualRoot.AttemptId != inputs.Operation.HandoffId)
            throw Invalid();
        var writer = new JournalWriter(Guid.NewGuid(), process);
        using var creator = Process.GetCurrentProcess();
        database.Write((connection, transaction) =>
        {
            actualJob.RequirePersistingRoot(actualRoot); restored.RequireInside(connection, transaction);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM main.journal_writers WHERE journal_id=$journal;";
            command.Parameters.AddWithValue("$journal", inputs.Plan.JournalId.ToString("D"));
            if ((long)command.ExecuteScalar()! is not (2 or 3)) throw Invalid();
            JournalWriterStore.Insert(connection, transaction, inputs.Plan.JournalId, writer);
            command.CommandText = "INSERT INTO main.journal_writer_jobs VALUES($journal,$writer,$job,$session);";
            command.Parameters.AddWithValue("$writer", writer.WriterId.ToString("D")); command.Parameters.AddWithValue("$job", actualJob.Name);
            command.Parameters.AddWithValue("$session", creator.SessionId); command.ExecuteNonQuery();
            RecordRoot(connection, transaction, inputs.Plan.JournalId, writer, actualJob.Name);
            return true;
        });
        return writer.WriterId;
    }

    internal void RequireRestoredBound(MaintenanceStartupInputs inputs)
    {
        RequireNativeBound(); inputs.RequireCurrent(_bootstrap.Inputs.Database, MaintenanceHelperFrames.Binding(_bootstrap.Inputs));
        if (inputs.Restored is not { } restored || restored.Helper.Writer != Writer || restored.Helper.JobName != JobName ||
            inputs.Plan.JournalId != _bootstrap.Inputs.Plan.JournalId) throw Invalid();
    }

    private static void RecordRoot(SqliteConnection connection, SqliteTransaction transaction, Guid journal,
        JournalWriter writer, string jobName)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO main.journal_writer_job_roots VALUES($journal,$writer,$job,$pid,$created,$path,$hash,$sid,$elevated);";
        command.Parameters.AddWithValue("$journal", journal.ToString("D")); command.Parameters.AddWithValue("$writer", writer.WriterId.ToString("D"));
        command.Parameters.AddWithValue("$job", jobName); command.Parameters.AddWithValue("$pid", writer.Process.ProcessId);
        command.Parameters.AddWithValue("$created", writer.Process.CreatedAtUtc.ToFileTimeUtc()); command.Parameters.AddWithValue("$path", writer.Process.ImagePath);
        command.Parameters.AddWithValue("$hash", writer.Process.ImageSha256); command.Parameters.AddWithValue("$sid", writer.Process.UserSid);
        command.Parameters.AddWithValue("$elevated", writer.Process.IsElevated ? 1 : 0);
        if (command.ExecuteNonQuery() != 1) throw Invalid();
    }

    internal MaintenanceJobQuery RetainJobQuery(MaintenanceStartupInputs? restored = null)
    {
        if (restored is null) RequireBound(); else RequireRestoredBound(restored);
        return MaintenanceJobQuery.Retain(_jobLifetime!);
    }

    internal void RequireCurrent(InstanceLease lease)
    {
        lease.VerifyHeld(_bootstrap.Inputs.Instance); RequireBound();
    }

    internal void RequireBound()
    {
        _bootstrap.RequireCurrent();
        RequireNativeBound();
        using var connection = DatabaseConnections.Open(_bootstrap.Inputs.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System);
        using var transaction = connection.BeginTransaction(deferred: true);
        RequireRegistered(connection, transaction, _bootstrap.Inputs.Plan.JournalId, Writer, JobName); transaction.Commit();
    }

    internal void RequireNativeBound()
    {
        if (_jobLifetime is not { IsClosed: false, IsInvalid: false } ||
            ProcessIdentityReader.Read(Environment.ProcessId) != Writer.Process || JobStateReader.Limits(_jobLifetime).Basic.Flags != 0x2800) throw Invalid();
        var accounting = JobStateReader.Accounting(_jobLifetime);
        if (accounting is not { TotalProcesses: 1, ActiveProcesses: 1 } ||
            !JobStateReader.ActiveProcessIds(_jobLifetime).SequenceEqual([Writer.Process.ProcessId])) throw Invalid();
    }

    internal void RequireRegisteredInside(SqliteConnection connection, SqliteTransaction transaction, Guid journalId)
    {
        if (_jobLifetime is not { IsClosed: false, IsInvalid: false } || journalId != _bootstrap.Inputs.Plan.JournalId ||
            ProcessIdentityReader.Read(Environment.ProcessId) != Writer.Process) throw Invalid();
        _bootstrap.Inputs.RequireInside(connection, transaction);
        RequireRegistered(connection, transaction, journalId, Writer, JobName);
    }

    private static void RequireRegistered(SqliteConnection connection, SqliteTransaction transaction,
        Guid journalId, JournalWriter writer, string jobName)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.journal_writers w JOIN main.journal_writer_jobs b USING(journal_id,writer_id) " +
            "JOIN main.operation_journals j USING(journal_id) JOIN system.maintenance_operations m ON m.operation_id=j.operation_id " +
            "WHERE w.journal_id=$journal AND w.writer_id=$writer AND b.job_name=$job AND m.state=1 AND m.handoff_id IS NOT NULL " +
            "AND j.operation_kind='updateApply' AND w.process_id=$pid AND w.created_file_time=$created " +
            "AND w.image_path=$path AND w.image_sha256=$hash AND w.user_sid=$sid AND w.elevated=$elevated);";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D")); command.Parameters.AddWithValue("$writer", writer.WriterId.ToString("D"));
        command.Parameters.AddWithValue("$job", jobName); command.Parameters.AddWithValue("$pid", writer.Process.ProcessId);
        command.Parameters.AddWithValue("$created", writer.Process.CreatedAtUtc.ToFileTimeUtc()); command.Parameters.AddWithValue("$path", writer.Process.ImagePath);
        command.Parameters.AddWithValue("$hash", writer.Process.ImageSha256); command.Parameters.AddWithValue("$sid", writer.Process.UserSid);
        command.Parameters.AddWithValue("$elevated", writer.Process.IsElevated ? 1 : 0);
        if ((long)command.ExecuteScalar()! != 1) throw Invalid();
    }

    private static bool RecognizedJobName(string value) => value is { Length: 50 } && value.StartsWith("Local\\NexusWorker-", StringComparison.Ordinal) &&
        Guid.TryParseExact(value[18..], "N", out _);

    public void Dispose() => Interlocked.Exchange(ref _jobLifetime, null)?.Dispose();
    private static IOException Invalid() => new("maintenance_helper_actor_unbound");
}
