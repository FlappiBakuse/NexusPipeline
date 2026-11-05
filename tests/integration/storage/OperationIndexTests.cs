using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class OperationIndexTests
{
    [Fact(DisplayName = "Admission Indexes Its Owned Copy Without Changing The Original Database And Enables Indexed Parent Reads")]
    public void PreservesInspectionSource()
    {
        var instance = CreateInstance(); var database = StateDatabase.CreateFresh(instance);
        using (var connection = DatabaseConnections.Open(database.DatabasePath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP INDEX operations_parent_read; DROP INDEX operations_kind_read;"; command.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(database.DatabasePath);
        var inspection = DatabaseInspector.Inspect(database.DatabasePath, instance.InstanceId, Path.Combine(instance.RuntimeRoot, "diagnostics"));
        try
        {
            using var copy = DatabaseConnections.Open(Path.Combine(inspection.DiagnosticCopyRoot, "nexus.db"), readOnly: true);
            Assert.Equal(StateDatabase.SchemaDigest, DatabaseSchema.VerifyCatalog(copy));
            using var query = copy.CreateCommand();
            query.CommandText = "EXPLAIN QUERY PLAN SELECT operation_id FROM operations WHERE initial_journal_id=$parent AND operation_kind='execution.cancel';";
            query.Parameters.AddWithValue("$parent", Guid.NewGuid().ToString("D"));
            using var rows = query.ExecuteReader(); Assert.True(rows.Read());
            Assert.Contains("SEARCH operations USING INDEX operations_", rows.GetString(3), StringComparison.Ordinal);
            Assert.DoesNotContain("SCAN", rows.GetString(3), StringComparison.Ordinal);
        }
        finally { inspection.OwnedCopy!.Discard(); }
        Assert.Equal(before, File.ReadAllBytes(database.DatabasePath));
        Assert.False(Directory.Exists(inspection.DiagnosticCopyRoot));
        using var lease = InstanceLease.Acquire(instance, new());
        StateDatabase.OpenExisting(instance, Path.Combine(instance.RuntimeRoot, "reopen"), new JournalStartupAdmission(instance, new()), lease);
        using var current = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var count = current.CreateCommand(); count.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name IN ('operations_parent_read','operations_kind_read');";
        Assert.Equal(2L, count.ExecuteScalar());
    }

    [Theory(DisplayName = "Unknown Or Altered Read Indexes Fail Closed And Preserve Original Bytes")]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsAlteredIndexes(bool unknown)
    {
        var instance = CreateInstance(); var database = StateDatabase.CreateFresh(instance);
        using (var connection = DatabaseConnections.Open(database.DatabasePath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = unknown ? "CREATE INDEX unknown_read ON operations(subject_id);" :
                "DROP INDEX operations_parent_read; CREATE UNIQUE INDEX operations_parent_read ON operations(initial_journal_id);";
            command.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(database.DatabasePath);
        Assert.Equal("unsupported_database_catalog", Assert.Throws<IOException>(() => DatabaseInspector.Inspect(database.DatabasePath,
            instance.InstanceId, Path.Combine(instance.RuntimeRoot, "diagnostics"))).Message);
        Assert.Equal(before, File.ReadAllBytes(database.DatabasePath));
    }

    [Fact(DisplayName = "Unbound Cancellation Is Rejected Before Parent Dispatch And By Complete Inventory")]
    public void RejectsUnboundCancellation()
    {
        var instance = CreateInstance(); var database = StateDatabase.CreateFresh(instance);
        database.Accept(new OperationIdentity(Guid.NewGuid(), "subject", new string('a', 64), "execution.cancel"), DateTimeOffset.UtcNow);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Equal("execution_cancel_operation_unbound", Assert.Throws<IOException>(() =>
            ExecutionCancellationRows.RequireRecognizedTargets(connection, transaction, Guid.NewGuid())).Message);
        Assert.Equal("execution_cancel_operation_unbound", Assert.Throws<IOException>(() =>
            ExecutionCancellationRows.RequireRecognizedTargets(connection, transaction)).Message);
        Assert.Equal("execution_cancel_operation_unbound", Assert.Throws<IOException>(() =>
            NexusPipeline.Platform.Storage.Execution.ExecutionCancellationStore.VerifyInventory(instance, database, new())).Message);
    }

    private static RuntimeInstance CreateInstance() => new InstanceRoot(new()).OpenOrCreate(Path.Combine(
        Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "operation-index-" + Guid.NewGuid().ToString("N")));
}
