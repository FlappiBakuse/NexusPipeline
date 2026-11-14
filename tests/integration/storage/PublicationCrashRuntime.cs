using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed record PublicationCrashRuntime(string Root, RuntimeInstance Instance,
    StateDatabase Database, InstanceLease Lease, Guid OperationId, Guid TaskId, BlobReference Parent) : IDisposable
{
    internal static async Task<PublicationCrashRuntime> CreateAsync(string mode)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "notification-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var reportPath = Path.Combine(root, "fault.json");
        using var owner = new OwnedCrashProcess();
        var identity = owner.Start(["notification-publication-recovery", root, mode, reportPath], root,
            Guid.NewGuid(), Guid.NewGuid(), Path.Combine(root, "process.json"));
        var report = await OwnedCrashProcess.ReadReportAsync(reportPath);
        Assert.Equal("faultReady", report.GetProperty("status").GetString());
        Assert.Equal(identity.ProcessId, report.GetProperty("processId").GetInt32());
        var instance = report.GetProperty("instance").Deserialize<RuntimeInstance>()!;
        var stopped = await owner.StopAsync(); Assert.True(stopped.CompleteJobStopped);
        Assert.NotEqual(0, await owner.AwaitExitAsync());
        var lease = InstanceLease.Acquire(instance, new());
        try
        {
            if (mode == "final-sql")
            {
                using var connection = DatabaseConnections.Open(Path.Combine(instance.RuntimeRoot, "state", "runtime.db"), domains: DatabaseDomain.Notifications);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT sql FROM notifications.sqlite_schema WHERE type='trigger' AND name='notification_final_fault';";
                Assert.Equal("CREATE TRIGGER notification_final_fault BEFORE INSERT ON notification_publication_outcomes BEGIN SELECT RAISE(ABORT,'notification_final_fault'); END",
                    command.ExecuteScalar());
                command.CommandText = "DROP TRIGGER notifications.notification_final_fault;";
                command.ExecuteNonQuery();
            }
            var database = StateDatabase.OpenExisting(instance, Path.Combine(root, "post-owner-inspection"));
            return new(root, instance, database, lease, report.GetProperty("operationId").GetGuid(),
                report.GetProperty("taskId").GetGuid(), report.GetProperty("parent").Deserialize<BlobReference>()!);
        }
        catch { lease.Dispose(); throw; }
    }

    internal NotificationPublicationSnapshot Read() => new NotificationPublicationCodec(Instance, new()).Read(Parent);

    internal string[] Snapshot(bool includeDatabaseBytes = true)
    {
        var result = new List<string>();
        using (var connection = DatabaseConnections.Open(Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            foreach (var table in new[] { "operations", "operation_journals", "journal_writers", "file_effects", "resource_isolations",
                "secret_fields", "secret_mutations", "secret_preparations", "secret_payloads", "notification_publications",
                "notification_publication_fields", "notification_publication_outcomes", "notification_catalog_current", "notification_task_ids" })
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT * FROM " + table + ";";
                using var rows = command.ExecuteReader();
                while (rows.Read()) result.Add(table + ":" + JsonSerializer.Serialize(Enumerable.Range(0, rows.FieldCount)
                    .Select(index => rows.IsDBNull(index) ? null : rows.GetValue(index)).ToArray()));
            }
        }
        foreach (var path in Directory.EnumerateFiles(Instance.RuntimeRoot, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(path) == "instance.lock" || path == Database.DatabasePath + "-wal" || path == Database.DatabasePath + "-shm" ||
                !includeDatabaseBytes && path == Database.DatabasePath) continue;
            result.Add(Path.GetRelativePath(Instance.RuntimeRoot, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    public void Dispose() => Lease.Dispose();
}
