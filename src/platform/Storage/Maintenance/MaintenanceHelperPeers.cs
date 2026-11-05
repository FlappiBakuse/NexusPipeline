using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed record MaintenanceHelperPeers(Guid WriterId, ProcessIdentity Parent);

[SupportedOSPlatform("windows")]
internal static class HelperPeerReader
{
    internal static MaintenanceHelperPeers Read(MaintenanceBootstrapPayload bootstrap)
    {
        bootstrap.RequireCurrent();
        var current = ProcessIdentityReader.Read(Environment.ProcessId); bootstrap.RequireHelper(current);
        using var connection = DatabaseConnections.Open(bootstrap.Inputs.Database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT writer_id,process_id,created_file_time,image_path,image_sha256,user_sid,elevated " +
            "FROM journal_writers WHERE journal_id=$journal LIMIT 3;";
        command.Parameters.AddWithValue("$journal", bootstrap.Inputs.Plan.JournalId.ToString("D"));
        var writers = new List<JournalWriter>();
        using (var rows = command.ExecuteReader())
            while (rows.Read()) writers.Add(new(Guid.Parse(rows.GetString(0)), new(rows.GetInt32(1), DateTime.FromFileTimeUtc(rows.GetInt64(2)),
                rows.GetString(3), rows.GetString(4), rows.GetString(5), rows.GetInt64(6) == 1)));
        if (writers.Count != 2) throw Invalid();
        var self = writers.SingleOrDefault(item => item.Process == current) ?? throw Invalid();
        var parent = writers.Single(item => item.WriterId != self.WriterId).Process;
        if (parent.UserSid != current.UserSid || parent.IsElevated != current.IsElevated) throw Invalid();
        var relative = Path.GetRelativePath(bootstrap.Inputs.Plan.TargetPath, parent.ImagePath).Replace('\\', '/');
        PhysicalPathPolicy.ValidateRelativePath(relative);
        var file = bootstrap.Inputs.Plan.Before.Snapshot.Entries.SingleOrDefault(item => item.RelativePath == relative) ?? throw Invalid();
        if (file.Kind != FileObjectKind.File || file.Sha256 != parent.ImageSha256 || !relative.EndsWith(".exe", StringComparison.Ordinal)) throw Invalid();
        var content = bootstrap.Inputs.Plan.Before.Contents.SingleOrDefault(item => item.SizeBytes == file.SizeBytes && item.Sha256 == file.Sha256) ?? throw Invalid();
        using var input = new ImmutableBlobStore(bootstrap.Inputs.Instance, bootstrap.Inputs.Contracts).OpenVerified(content);
        UpdateBuildProfile.RequireBundle(input, "NexusPipeline.Host.dll", bootstrap.Inputs.Manifest.Profile);
        bootstrap.RequireCurrent();
        return new(self.WriterId, parent);
    }
    private static IOException Invalid() => new("maintenance_helper_parent_unbound");
}
