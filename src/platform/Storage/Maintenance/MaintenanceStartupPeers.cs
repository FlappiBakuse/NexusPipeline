using System.Runtime.Versioning;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

[SupportedOSPlatform("windows")]
internal static class MaintenanceStartupPeers
{
    internal static ProcessIdentity RequireHelper(MaintenanceBootstrapPayload bootstrap, string startupJob, RestoredStartupEvidence? restored = null)
    {
        bootstrap.RequireCurrent();
        var inputs = bootstrap.Inputs;
        var current = ProcessIdentityReader.Read(Environment.ProcessId);
        using var connection = DatabaseConnections.Open(inputs.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT w.process_id,w.created_file_time,w.image_path,w.image_sha256,w.user_sid,w.elevated,b.job_name " +
            "FROM main.journal_writers w LEFT JOIN main.journal_writer_jobs b USING(journal_id,writer_id) WHERE w.journal_id=$journal LIMIT 5;";
        command.Parameters.AddWithValue("$journal", inputs.Plan.JournalId.ToString("D"));
        var writers = new List<(ProcessIdentity Process, string? Job)>();
        using (var rows = command.ExecuteReader())
            while (rows.Read()) writers.Add((new(rows.GetInt32(0), DateTime.FromFileTimeUtc(rows.GetInt64(1)),
                rows.GetString(2), rows.GetString(3), rows.GetString(4), rows.GetInt32(5) == 1), rows.IsDBNull(6) ? null : rows.GetString(6)));
        if ((restored is null ? writers.Count != 3 : writers.Count is not (3 or 4)) ||
            writers.Count(item => item.Process == current && item.Job == startupJob) != 1) throw Invalid();
        if (restored is not null)
        {
            restored.RequireCurrent(); restored.RequireHost(current);
            if (restored.Bound.Plan.JournalId != inputs.Plan.JournalId) throw Invalid();
        }
        var helper = writers.SingleOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.Process.ImagePath, bootstrap.HelperPath));
        if (helper.Process is null || helper.Job is null || helper.Process.UserSid != current.UserSid ||
            helper.Process.IsElevated != current.IsElevated) throw Invalid();
        bootstrap.RequireHelper(helper.Process);
        if (restored is not null && (helper.Process != restored.Helper.Writer.Process || helper.Job != restored.Helper.JobName)) throw Invalid();
        using var peer = new Networking.NativeClientBinding(helper.Process);
        if (!peer.IsLive) throw Invalid();
        return helper.Process;
    }
    private static IOException Invalid() => new("maintenance_startup_producers_unbound");
}
