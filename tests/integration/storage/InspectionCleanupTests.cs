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
        using (var connection = DatabaseConnections.Open(database.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=DELETE; CREATE TABLE inspection_fixture(value BLOB);";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO inspection_fixture VALUES(zeroblob(67108864));";
            for (var count = 0; count < 17; count++) command.ExecuteNonQuery();
            command.CommandText = "DROP TABLE inspection_fixture;"; command.ExecuteNonQuery();
        }
        Assert.True(new FileInfo(database.DatabasePath).Length > 1_073_741_824);
        var identity = PhysicalObjectIdentity.Read(database.DatabasePath);
        var diagnostic = Path.Combine(root, "diagnostic");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            _ = Reopen(instance, diagnostic, attempt);
            Assert.Equal(identity, PhysicalObjectIdentity.Read(database.DatabasePath));
            Assert.Empty(Directory.EnumerateDirectories(diagnostic));
        }
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
