using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class DatabaseCrashFixture
{
    internal static int Run(string[] args)
    {
        if (args is not [var root, var phase, var report] || phase is not ("before" or "during" or "after")) return 2;
        OwnedFixtureEvidence.RequirePath(root); OwnedFixtureEvidence.RequirePath(report);
        var instance = new InstanceRoot(new ContractReader()).OpenExisting(root);
        using var lease = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.CreateFresh(instance, lease);
        var operation = Guid.NewGuid();
        var content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { value = new string('x', 524288) }));
        var digest = Convert.ToHexStringLower(SHA256.HashData(content));
        using var pause = phase == "during" ? new DatabaseCommitPause(root, () => Report(report, instance, operation, phase)) : null;
        database.Write((connection, write) =>
        {
            using var cache = connection.CreateCommand(); cache.Transaction = write;
            cache.CommandText = "PRAGMA extensions.cache_size=8;"; cache.ExecuteNonQuery();
            OperationRows.Accept(connection, write, new OperationIdentity(operation, "fixture", digest, "fixture.database"), DateTimeOffset.UtcNow);
            using var document = connection.CreateCommand(); document.Transaction = write;
            document.CommandText = "INSERT INTO extensions.extension_documents VALUES('fixture','database',1,$content,$digest,$operation);";
            document.Parameters.AddWithValue("$content", content); document.Parameters.AddWithValue("$digest", digest);
            document.Parameters.AddWithValue("$operation", operation.ToString("D")); document.ExecuteNonQuery();
            document.Parameters.Clear();
            document.CommandText = "UPDATE system.startup_registration_current SET command_json=$value WHERE singleton=1;";
            document.Parameters.AddWithValue("$value", JsonSerializer.SerializeToUtf8Bytes(new { operation })); document.ExecuteNonQuery();
            if (phase == "before") { Report(report, instance, operation, phase); WaitForStop(); }
            pause?.Arm();
            return true;
        });
        if (phase == "after") Report(report, instance, operation, phase);
        WaitForStop();
        return 0;
    }

    private static void Report(string path, RuntimeInstance instance, Guid operation, string phase)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        output.Write(JsonSerializer.SerializeToUtf8Bytes(new { instanceId = instance.InstanceId, operation, phase }));
        output.Flush(flushToDisk: true);
    }

    private static void WaitForStop()
    {
        using var waiting = new ManualResetEvent(false);
        if (!waiting.WaitOne(TimeSpan.FromSeconds(30))) throw new TimeoutException("database_fixture_not_stopped");
    }
}
