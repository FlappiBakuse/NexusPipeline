using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

internal static class LegacySchemaChecks
{
    internal static void Reject(string stage)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "legacy-schema-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var current = StateDatabase.CreateFresh(instance);
        var property = typeof(LegacyStateSchema).GetProperty(stage + "Sql", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var sql = (string)property.GetValue(null)!;
        var digest = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sql)));
        var historical = Path.Combine(root, "historical.db");
        using (var connection = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open($"Data Source={historical};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql; command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO main.schema_info VALUES(1,'nexus.state.database',1,$instance,$digest,'2026-11-01T00:00:00.0000000Z'); PRAGMA user_version=1;";
            command.Parameters.AddWithValue("$instance", instance.InstanceId.ToString("D"));
            command.Parameters.AddWithValue("$digest", digest); command.ExecuteNonQuery();
        }
        File.Copy(historical, current.DatabasePath, overwrite: true);
        using var lease = InstanceLease.Acquire(instance, new());
        var before = Files(instance.RuntimeRoot);
        Assert.Throws<IOException>(() => StateDatabase.OpenExisting(instance, Path.Combine(root, "unowned"), new JournalStartupAdmission(instance, new())));
        Assert.Equal(before, Files(instance.RuntimeRoot));
        Assert.Throws<IOException>(() => StateDatabase.OpenExisting(instance, Path.Combine(root, "owned"), new JournalStartupAdmission(instance, new()), lease));
        Assert.Equal(before, Files(instance.RuntimeRoot));
    }

    private static string[] Files(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) != "instance.lock").Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" +
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}
