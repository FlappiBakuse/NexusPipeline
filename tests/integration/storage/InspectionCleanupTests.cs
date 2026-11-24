using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

public sealed class InspectionCleanupTests(ITestOutputHelper output)
{
    [Fact(DisplayName = "A Small Database Reopens With Its Accepted Operation Intact")]
    public void ReopensSmallDatabase()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "small-inspection-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var identity = new NexusPipeline.Core.Operations.OperationIdentity(Guid.NewGuid(), "fixture", new string('a', 64), "fixture.command");
        database.Accept(identity, DateTimeOffset.UtcNow);
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var reopened = Reopen(instance, Path.Combine(root, "diagnostic"), repeat);
            Assert.False(reopened.Accept(identity, DateTimeOffset.UtcNow).IsNew);
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, "diagnostic")));
        }
    }

    [Fact(DisplayName = "A Valid Database Larger Than One GiB Reopens And Removes Only Its Inspection Copy")]
    public void ReopensLargeDatabase()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "large-inspection-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        using (var connection = DatabaseConnections.Open(database.DatabasePath, domains: DatabaseDomain.Runtime))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=DELETE; CREATE TABLE inspection_fixture(value BLOB);";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO inspection_fixture VALUES(zeroblob(67108864));";
            for (var count = 0; count < 17; count++) command.ExecuteNonQuery();
            command.CommandText = "DROP TABLE inspection_fixture;"; command.ExecuteNonQuery();
        }
        Assert.True(new FileInfo(database.DatabasePath).Length > 1_073_741_824);
        var stateDirectory = Path.GetDirectoryName(database.DatabasePath)!;
        var original = Inventory(stateDirectory);
        Assert.Contains(original, entry => entry.Path == "runtime.db" && entry.Bytes > 1_073_741_824);
        Assert.Contains(original, entry => entry.Path == "records.db");
        Assert.Contains(original, entry => entry.Path == "system.db");
        var diagnostic = Path.Combine(root, "diagnostic");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            _ = Reopen(instance, diagnostic, attempt);
            Assert.Equal(original, Inventory(stateDirectory));
            Assert.Empty(Directory.EnumerateFileSystemEntries(diagnostic));
        }
    }

    private static (string Path, bool IsDirectory, long Bytes, string? Sha256, PhysicalObjectIdentity Identity)[] Inventory(string root)
    {
        return Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path =>
        {
            var relative = Path.GetRelativePath(root, path);
            var identity = PhysicalObjectIdentity.Read(path);
            if (Directory.Exists(path)) return (relative, true, 0L, (string?)null, identity);
            using var input = File.OpenRead(path);
            return (relative, false, input.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(input)), identity);
        }).ToArray();
    }
    private StateDatabase Reopen(RuntimeInstance instance, string diagnostic, int repeat)
    {
        var previous = Console.Error;
        using var captured = new StringWriter();
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Console.SetError(captured);
            return StateDatabase.OpenExisting(instance, diagnostic);
        }
        finally
        {
            Console.SetError(previous);
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { kind = "startup.fixture", repeat,
                elapsedMilliseconds = started.Elapsed.TotalMilliseconds, scope = "database admission and copy cleanup; OS cache not flushed" }));
            output.WriteLine(captured.ToString());
        }
    }
}
