using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class OperationIndexTests
{
    [Fact(DisplayName = "Inspection Repairs Only Its Copy And Exclusive Startup Repairs The Admitted Database")]
    public void PreservesInspectionSource()
    {
        var instance = CreateInstance(); var database = StateDatabase.CreateFresh(instance);
        using (var connection = DatabaseConnections.Open(database.DatabasePath, domains: DatabaseDomain.Runtime))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP INDEX operations_parent_read; DROP INDEX operations_kind_read; DROP INDEX account_run_records_ended_read;";
            command.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(database.DatabasePath);
        var inspection = DatabaseInspector.Inspect(database.DatabasePath, instance.InstanceId, Path.Combine(instance.RuntimeRoot, "diagnostics"));
        const string restoredIndexes = "SELECT count(*) FROM sqlite_schema WHERE name IN " +
            "('operations_parent_read','operations_kind_read','account_run_records_ended_read');";
        try
        {
            using var copy = DatabaseConnections.Open(Path.Combine(inspection.DiagnosticCopyRoot, "runtime.db"), readOnly: true, domains: DatabaseDomain.Runtime);
            Assert.Equal(StateDatabase.SchemaDigest, DatabaseSchema.VerifyCatalog(copy));
            Assert.Equal(3L, DatabaseConnections.Scalar(copy, restoredIndexes));
        }
        finally { inspection.OwnedCopy!.Discard(); }
        Assert.Equal(before, File.ReadAllBytes(database.DatabasePath));
        Assert.False(Directory.Exists(inspection.DiagnosticCopyRoot));
        using var lease = InstanceLease.Acquire(instance, new());
        StateDatabase.OpenExisting(instance, Path.Combine(instance.RuntimeRoot, "reopen"), new JournalStartupAdmission(instance, new()), lease);
        using var current = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        Assert.Equal(3L, DatabaseConnections.Scalar(current, restoredIndexes));
    }

    [Theory(DisplayName = "Unknown Or Altered Read Indexes Fail Closed And Preserve Original Bytes")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RejectsAlteredIndexes(int kind)
    {
        var instance = CreateInstance(); var database = StateDatabase.CreateFresh(instance);
        using (var connection = DatabaseConnections.Open(database.DatabasePath, domains: DatabaseDomain.Runtime))
        {
            using var command = connection.CreateCommand();
            command.CommandText = kind switch
            {
                0 => "CREATE INDEX unknown_read ON operations(subject_id);",
                1 => "DROP INDEX operations_parent_read; CREATE UNIQUE INDEX operations_parent_read ON operations(initial_journal_id);",
                _ => "DROP INDEX account_run_records_ended_read; CREATE INDEX account_run_records_ended_read ON account_run_records(user_id);"
            };
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
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
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
