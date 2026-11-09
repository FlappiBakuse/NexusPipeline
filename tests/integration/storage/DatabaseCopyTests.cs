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
    [InlineData("bytes", "inspection_copy_changed")]
    public void PreservesFailedCopies(string fault, string expected)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "copy-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var original = new OperationIdentity(Guid.NewGuid(), "fixture", new string('a', 64), "fixture.command");
        var late = new OperationIdentity(Guid.NewGuid(), "fixture", new string('b', 64), "fixture.command");
        database.Accept(original, DateTimeOffset.UtcNow);
        var sourceIdentity = PhysicalObjectIdentity.Read(database.DatabasePath);
        var expectedSource = ReadGroup(database.DatabasePath);
        var diagnostic = Path.Combine(root, "diagnostic");
        string? copyPath = null;
        PhysicalObjectIdentity? copyIdentity = null;
        byte[]? copyBytes = null;
        var admission = new Admission(inspection =>
        {
            var path = Path.Combine(inspection.DiagnosticCopyRoot, "nexus.db");
            var inspected = PhysicalObjectIdentity.Read(path);
            if (fault == "source")
            {
                database.Accept(late, DateTimeOffset.UtcNow);
                expectedSource = ReadGroup(database.DatabasePath);
            }
            if (fault == "unknown") File.WriteAllText(Path.Combine(inspection.DiagnosticCopyRoot, "keep.bin"), "keep");
            if (fault == "bytes")
            {
                var changed = File.ReadAllBytes(path);
                changed[^1] ^= byte.MaxValue;
                File.WriteAllBytes(path, changed);
                Assert.Equal(inspected, PhysicalObjectIdentity.Read(path));
            }
            if (fault == "replacement")
            {
                var candidate = path + ".candidate";
                var originalBytes = File.ReadAllBytes(path);
                File.WriteAllBytes(candidate, originalBytes);
                File.Move(candidate, path, overwrite: true);
                Assert.NotEqual(inspected, PhysicalObjectIdentity.Read(path));
                Assert.Equal(originalBytes, File.ReadAllBytes(path));
            }
            copyPath = path;
            copyIdentity = PhysicalObjectIdentity.Read(path);
            copyBytes = File.ReadAllBytes(path);
            if (fault == "admission") throw new IOException("fixture_admission_failed");
        });
        Assert.Equal(expected, Assert.Throws<IOException>(() => StateDatabase.OpenExisting(instance, diagnostic, admission)).Message);
        Assert.NotNull(copyPath);
        Assert.Equal(copyIdentity, PhysicalObjectIdentity.Read(copyPath));
        Assert.Equal(copyBytes, File.ReadAllBytes(copyPath));
        if (fault == "unknown") Assert.Equal("keep", File.ReadAllText(Path.Combine(Path.GetDirectoryName(copyPath)!, "keep.bin")));
        var persisted = ReadGroup(database.DatabasePath);
        Assert.Equal(expectedSource.Keys.Order(), persisted.Keys.Order());
        foreach (var suffix in expectedSource.Keys) Assert.Equal(expectedSource[suffix], persisted[suffix]);
        Assert.Equal(sourceIdentity, PhysicalObjectIdentity.Read(database.DatabasePath));
        var originalResult = new OperationSnapshot(original.OperationId, original.Kind, "accepted", null);
        Assert.Equal(originalResult, ReadOperation(database.DatabasePath, original));
        Assert.Equal(fault == "source" ? new OperationSnapshot(late.OperationId, late.Kind, "accepted", null) : null,
            ReadOperation(database.DatabasePath, late));
        if (fault != "bytes")
        {
            Assert.Equal(originalResult, ReadOperation(copyPath, original));
            Assert.Null(ReadOperation(copyPath, late));
        }
    }

    private static Dictionary<string, byte[]> ReadGroup(string path) => new[] { "", "-wal", "-shm", "-journal" }
        .Where(suffix => File.Exists(path + suffix)).ToDictionary(suffix => suffix, suffix => File.ReadAllBytes(path + suffix), StringComparer.Ordinal);

    private static OperationSnapshot? ReadOperation(string path, OperationIdentity operation)
    {
        using var connection = DatabaseConnections.Open(path, readOnly: true, allowPooling: false);
        using var transaction = connection.BeginTransaction(deferred: true);
        return OperationRows.ReadOwned(connection, transaction, operation.OperationId, operation.SubjectId);
    }

    private sealed class Admission(Action<DatabaseInspection> verify) : IDatabaseStartupAdmission
    {
        public void Verify(DatabaseInspection inspection) => verify(inspection);
    }
}
