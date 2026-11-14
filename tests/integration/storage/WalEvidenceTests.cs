using System.Security.Cryptography;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class WalEvidenceTests
{
    [Theory(DisplayName = "Actual Host Refuses Unexpected Sqlite Sidecars And Preserves The Complete Database Set")]
    [InlineData("runtime.db-wal")]
    [InlineData("system.db-shm")]
    [InlineData("runtime.db-journal")]
    [InlineData("unknown.db")]
    public async Task PreservesUnverifiedGroups(string name)
    {
        var (root, instance, database) = Create();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, name), "unknown SQLite evidence");
        var before = Fingerprints(instance.RuntimeRoot);
        using var job = new OwnedWorkerJob();
        var identity = job.Start(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!,
            ["serve", "--runtime-root", instance.RuntimeRoot, "--port", "0"], root, instance.InstanceId, Guid.NewGuid(),
            observed => File.WriteAllBytes(Path.Combine(root, "actual-host.launch.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(observed)));
        using var process = System.Diagnostics.Process.GetProcessById(identity.ProcessId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(1, process.ExitCode);
        for (var count = 0; count < 250 && job.GetAccounting().ActiveProcesses != 0; count++) await Task.Delay(20);
        Assert.Equal(0u, job.GetAccounting().ActiveProcesses);
        Assert.Empty(job.ActiveProcessIds());
        Assert.Equal(before, Fingerprints(instance.RuntimeRoot));
    }

    [Theory(DisplayName = "Unknown Database Entries And Rollback Headers Remain Byte Identical During Inspection")]
    [InlineData("runtime.db-wal")]
    [InlineData("records.db-shm")]
    [InlineData("configuration.db-journal")]
    [InlineData("unrecognized-file")]
    public void PreservesUnknownEvidence(string name)
    {
        var (root, instance, database) = Create();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, name), "preserve unknown evidence");
        var before = Fingerprints(instance.RuntimeRoot);
        Assert.Throws<IOException>(() => DatabaseInspector.Inspect(database.DatabasePath, instance.InstanceId, Path.Combine(root, "diagnostics")));
        Assert.Equal(before, Fingerprints(instance.RuntimeRoot));
    }

    [Fact(DisplayName = "Persistent Inactive Rollback Files Are Inspected In An Independent Copy")]
    public void InspectsCompleteGroups()
    {
        var (root, instance, database) = Create();
        var identity = new NexusPipeline.Core.Operations.OperationIdentity(Guid.NewGuid(), "fixture", new string('a', 64), "fixture.command");
        database.Accept(identity, DateTimeOffset.UtcNow);
        var before = Fingerprints(instance.RuntimeRoot);
        var inspected = DatabaseInspector.Inspect(database.DatabasePath, instance.InstanceId, Path.Combine(root, "diagnostics"));
        try
        {
            Assert.Equal(8, inspected.Databases.Count);
            Assert.Equal(before, Fingerprints(instance.RuntimeRoot));
            using var connection = DatabaseConnections.Open(Path.Combine(inspected.DiagnosticCopyRoot, "runtime.db"), readOnly: true,
                domains: DatabaseDomain.Runtime);
            using var query = connection.CreateCommand(); query.CommandText = "SELECT count(*) FROM main.operations;";
            Assert.Equal(1L, query.ExecuteScalar());
        }
        finally { inspected.OwnedCopy?.Discard(); }
    }

    private static (string Root, RuntimeInstance Instance, StateDatabase Database) Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        return (root, instance, StateDatabase.CreateFresh(instance));
    }

    private static string[] Fingerprints(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Where(path => Path.GetFileName(path) != "instance.lock")
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}
