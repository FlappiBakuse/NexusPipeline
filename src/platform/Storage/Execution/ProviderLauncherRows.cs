using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ProviderLauncherRows
{
    internal static IEnumerable<Guid> ReadRecoveryParents(StateDatabase database)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        var after = "";
        while (true)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT r.journal_id FROM execution_provider_launcher_roots r " +
                "WHERE r.journal_id>$after AND NOT EXISTS(SELECT 1 FROM execution_provider_launcher_ended e " +
                "WHERE e.journal_id=r.journal_id AND e.launcher_id=r.launcher_id) ORDER BY r.journal_id LIMIT 64;";
            command.Parameters.AddWithValue("$after", after);
            var ids = new List<Guid>();
            using (var row = command.ExecuteReader()) while (row.Read()) ids.Add(Guid.ParseExact(row.GetString(0), "D"));
            foreach (var id in ids) yield return id;
            if (ids.Count < 64) yield break;
            after = ids[^1].ToString("D");
        }
    }

    internal static ProcessIdentity Creator(StateDatabase database, ProviderLauncherBinding binding)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT process_id,created_file_time,image_path,image_sha256,user_sid,elevated FROM journal_writers " +
            "WHERE journal_id=$journal AND writer_id=$writer;";
        command.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D")); command.Parameters.AddWithValue("$writer", binding.OwnerWriterId.ToString("D"));
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetInt32(0), DateTime.FromFileTimeUtc(row.GetInt64(1)), row.GetString(2), row.GetString(3), row.GetString(4), row.GetBoolean(5)) : throw Invalid();
    }

    internal static ProcessIdentity? Root(StateDatabase database, ProviderLauncherBinding binding)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = RootInside(connection, transaction, binding); transaction.Commit(); return result;
    }

    internal static ProcessIdentity? RootInside(SqliteConnection connection, SqliteTransaction transaction, ProviderLauncherBinding binding)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT step_id,job_name,writer_id,session_id,total_plan_sha256,activation_id,extension_id,entry_id," +
            "process_id,created_file_time,image_path,image_sha256,user_sid,elevated FROM execution_provider_launcher_roots " +
            "WHERE journal_id=$journal AND launcher_id=$launcher;";
        command.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$launcher", binding.LauncherId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        if (row.GetString(0) != binding.StepId || row.GetString(1) != binding.JobName || row.GetString(2) != binding.OwnerWriterId.ToString("D") ||
            row.GetInt32(3) != binding.SessionId || row.GetString(4) != binding.TotalPlanSha256 ||
            row.GetString(5) != binding.ActivationId.ToString("D") || row.GetString(6) != binding.ExtensionId || row.GetString(7) != binding.EntryId)
            throw Invalid();
        return new(row.GetInt32(8), DateTime.FromFileTimeUtc(row.GetInt64(9)), row.GetString(10), row.GetString(11), row.GetString(12), row.GetBoolean(13));
    }

    internal static void InsertRootInside(SqliteConnection connection, SqliteTransaction transaction, AttemptWriterState state,
        ProviderLauncherBinding binding, ProcessIdentity root)
    {
        var prior = RootInside(connection, transaction, binding);
        if (prior is not null) { if (prior != root) throw Invalid(); return; }
        if (!OperatingSystem.IsWindows() || binding.OwnerWriterId != JournalWriterStore.CurrentWriter.WriterId ||
            ProcessIdentityReader.Read(Environment.ProcessId) != JournalWriterStore.CurrentWriter.Process) throw Invalid();
        var current = ProviderLauncherCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .SingleOrDefault(item => item.Binding == binding);
        var image = state.Total.Steps.Single(step => step.StepId == binding.StepId).Provider!.Launcher!.Image;
        if (current?.Stage != "registered" || !StringComparer.OrdinalIgnoreCase.Equals(root.ImagePath, image.Path) ||
            root.ImageSha256 != image.Sha256) throw Invalid();
        ProviderLauncherProjection.RequireNewEffectInside(connection, transaction, state, binding.StepId);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO execution_provider_launcher_roots(journal_id,launcher_id,step_id,job_name,writer_id,session_id," +
            "total_plan_sha256,activation_id,extension_id,entry_id,process_id,created_file_time,image_path,image_sha256,user_sid,elevated) " +
            "VALUES($journal,$launcher,$step,$job,$writer,$session,$plan,$activation,$extension,$entry,$pid,$created,$path,$sha,$sid,$elevated);";
        command.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$launcher", binding.LauncherId.ToString("D")); command.Parameters.AddWithValue("$step", binding.StepId);
        command.Parameters.AddWithValue("$job", binding.JobName); command.Parameters.AddWithValue("$writer", binding.OwnerWriterId.ToString("D"));
        command.Parameters.AddWithValue("$session", binding.SessionId); command.Parameters.AddWithValue("$plan", binding.TotalPlanSha256);
        command.Parameters.AddWithValue("$activation", binding.ActivationId.ToString("D")); command.Parameters.AddWithValue("$extension", binding.ExtensionId);
        command.Parameters.AddWithValue("$entry", binding.EntryId); command.Parameters.AddWithValue("$pid", root.ProcessId);
        command.Parameters.AddWithValue("$created", root.CreatedAtUtc.ToFileTimeUtc()); command.Parameters.AddWithValue("$path", root.ImagePath);
        command.Parameters.AddWithValue("$sha", root.ImageSha256); command.Parameters.AddWithValue("$sid", root.UserSid);
        command.Parameters.AddWithValue("$elevated", root.IsElevated); command.ExecuteNonQuery();
    }

    internal static BlobReference? Ended(StateDatabase database, ProviderLauncherBinding binding, BlobReference retained)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT e.retained_path,e.retained_size,e.retained_sha256,e.artifact_path,e.artifact_size,e.artifact_sha256 " +
            "FROM execution_provider_launcher_ended e JOIN operations o ON o.operation_id=e.operation_id " +
            "JOIN operation_journals p ON p.journal_id=e.journal_id JOIN operations parent ON parent.operation_id=p.operation_id " +
            "WHERE e.journal_id=$journal AND e.launcher_id=$launcher AND o.subject_id=parent.subject_id " +
            "AND o.operation_kind='execution.provider-launcher-ended' AND o.status='completed' AND o.request_digest=e.artifact_sha256;";
        command.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$launcher", binding.LauncherId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        if (new BlobReference(row.GetString(0), row.GetInt64(1), row.GetString(2)) != retained) throw Invalid();
        var artifact = new BlobReference(row.GetString(3), row.GetInt64(4), row.GetString(5));
        StepEvidenceCodec.Plan(artifact); return artifact;
    }

    internal static void InsertEndedInside(SqliteConnection connection, SqliteTransaction transaction,
        OperationIdentity identity, ProviderLauncherBinding binding, BlobReference retained, BlobReference artifact)
    {
        OperationRows.Accept(connection, transaction, identity, DateTimeOffset.UtcNow);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO execution_provider_launcher_ended(journal_id,launcher_id,operation_id," +
            "retained_path,retained_size,retained_sha256,artifact_path,artifact_size,artifact_sha256) " +
            "VALUES($journal,$launcher,$operation,$retained,$retainedSize,$retainedSha,$artifact,$artifactSize,$artifactSha) " +
            "ON CONFLICT(journal_id,launcher_id) DO NOTHING;";
        command.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$launcher", binding.LauncherId.ToString("D"));
        command.Parameters.AddWithValue("$operation", identity.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$retained", retained.RelativePath); command.Parameters.AddWithValue("$retainedSize", retained.SizeBytes);
        command.Parameters.AddWithValue("$retainedSha", retained.Sha256); command.Parameters.AddWithValue("$artifact", artifact.RelativePath);
        command.Parameters.AddWithValue("$artifactSize", artifact.SizeBytes); command.Parameters.AddWithValue("$artifactSha", artifact.Sha256);
        command.ExecuteNonQuery();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM execution_provider_launcher_ended WHERE journal_id=$journal AND launcher_id=$launcher " +
            "AND operation_id=$operation AND retained_path=$retained AND retained_size=$retainedSize AND retained_sha256=$retainedSha " +
            "AND artifact_path=$artifact AND artifact_size=$artifactSize AND artifact_sha256=$artifactSha);";
        if ((long)command.ExecuteScalar()! != 1) throw Invalid();
        command.CommandText = "UPDATE operations SET status='completed' WHERE operation_id=$operation;"; command.ExecuteNonQuery();
    }

    internal static void Verify(StateDatabase database, System.Text.Json.JsonElement journal,
        NexusPipeline.Platform.Serialization.Protocol.ExecutionPlan total, string totalSha)
    {
        var jobs = ExecutionInvocationCodec.Views(journal).SelectMany(view => ProviderLauncherCodec.Read(view, total, totalSha))
            .GroupBy(item => item.Binding.LauncherId).Select(group => group.Last()).ToArray();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT launcher_id,job_name FROM execution_provider_launcher_roots WHERE journal_id=$journal LIMIT 1025;";
        command.Parameters.AddWithValue("$journal", journal.GetProperty("journalId").GetString()!);
        var rows = new List<(Guid Id, string Job)>();
        using (var read = command.ExecuteReader()) while (read.Read()) rows.Add((Guid.Parse(read.GetString(0)), read.GetString(1)));
        if (rows.Count > 1024 || rows.Any(row => !jobs.Any(job => job.Binding.LauncherId == row.Id && job.Binding.JobName == row.Job))) throw Invalid();
        foreach (var job in jobs)
        {
            var root = RootInside(connection, transaction, job.Binding);
            if (job.Stage is not ("registered" or "stopped" or "recoveredStopped") && root is null) throw Invalid();
            if (root is not null)
            {
                var image = total.Steps.Single(step => step.StepId == job.Binding.StepId).Provider!.Launcher!.Image;
                if (root.ImageSha256 != image.Sha256 || !StringComparer.OrdinalIgnoreCase.Equals(root.ImagePath, image.Path)) throw Invalid();
            }
        }
        command.CommandText = "SELECT launcher_id FROM execution_provider_launcher_ended WHERE journal_id=$journal LIMIT 1025;";
        using (var read = command.ExecuteReader())
            while (read.Read())
                if (!jobs.Any(job => job.Binding.LauncherId.ToString("D") == read.GetString(0) && job.Stage == "retained")) throw Invalid();
        transaction.Commit();
        foreach (var job in jobs.Where(job => job.Stage == "retained"))
        {
            // Presence and semantic validity are distinct; a damaged row must not look like no ending fact.
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT count(*) FROM execution_provider_launcher_ended WHERE journal_id=$journal AND launcher_id=$launcher;";
            check.Parameters.AddWithValue("$journal", job.Binding.ParentJournalId.ToString("D")); check.Parameters.AddWithValue("$launcher", job.Binding.LauncherId.ToString("D"));
            if ((long)check.ExecuteScalar()! > 0 && Ended(database, job.Binding, job.Artifact!) is null) throw Invalid();
        }
    }

    private static IOException Invalid() => new("execution_provider_launcher_sql_unbound");
}
