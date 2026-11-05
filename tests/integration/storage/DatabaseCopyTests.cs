using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class DatabaseCopyTests
{
    [Theory]
    [InlineData("admission", "fixture_admission_failed")]
    [InlineData("source", "database_group_changed_after_inspection")]
    [InlineData("unknown", "inspection_copy_unknown_entry")]
    [InlineData("replacement", "inspection_copy_changed")]
    public void PreservesFailedCopies(string fault, string expected)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "copy-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var diagnostic = Path.Combine(root, "diagnostic");
        var admission = new Admission(inspection =>
        {
            if (fault == "admission") throw new IOException("fixture_admission_failed");
            if (fault == "source") database.Accept(new OperationIdentity(Guid.NewGuid(), "fixture", new string('a', 64), "fixture.command"), DateTimeOffset.UtcNow);
            if (fault == "unknown") File.WriteAllText(Path.Combine(inspection.DiagnosticCopyRoot, "keep.bin"), "keep");
            if (fault == "replacement")
            {
                var path = Path.Combine(inspection.DiagnosticCopyRoot, "nexus.db");
                var candidate = path + ".candidate";
                File.WriteAllBytes(candidate, File.ReadAllBytes(path)); File.Move(candidate, path, overwrite: true);
            }
        });
        Assert.Equal(expected, Assert.Throws<IOException>(() => StateDatabase.OpenExisting(instance, diagnostic, admission)).Message);
        var copy = Assert.Single(Directory.EnumerateDirectories(diagnostic));
        Assert.True(File.Exists(Path.Combine(copy, "nexus.db")));
        if (fault == "unknown") Assert.Equal("keep", File.ReadAllText(Path.Combine(copy, "keep.bin")));
        if (fault != "source") Assert.Equal(0L, Count(database));
    }

    private static long Count(StateDatabase database)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT count(*) FROM operations;";
        return (long)command.ExecuteScalar()!;
    }

    private sealed class Admission(Action<DatabaseInspection> verify) : IDatabaseStartupAdmission
    {
        public void Verify(DatabaseInspection inspection) => verify(inspection);
    }
}
