using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Operations;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class OperationIndexTests
{
    [Fact(DisplayName = "Inspection Repairs Only Its Copy And Exclusive Startup Repairs The Admitted Database")]
    public void PreservesInspectionSource()
    {
        var instance = CreateInstance(); var database = StateDatabase.CreateFresh(instance);
        var operation = new OperationIdentity(Guid.NewGuid(), "fixture-inspection", new string('a', 64), "fixture.command");
        Assert.True(database.Accept(operation, DateTimeOffset.UtcNow).IsNew);
        var automation = Guid.NewGuid();
        var older = RecordStoreFixture.Record(84, automation);
        var middle = RecordStoreFixture.Record(93, automation);
        var latest = RecordStoreFixture.Record(105, automation);
        new RunRecordStore(database).AppendCompleted([older, middle, latest]);
        using (var connection = DatabaseConnections.Open(database.DatabasePath, domains: DatabaseDomain.Runtime | DatabaseDomain.Records))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP INDEX main.operations_parent_read; DROP INDEX main.operations_kind_read; DROP INDEX records.account_run_records_ended_read;";
            command.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(database.DatabasePath);
        var recordsPath = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "records.db");
        var recordsBefore = File.ReadAllBytes(recordsPath);
        var inspection = DatabaseInspector.Inspect(database.DatabasePath, instance.InstanceId, Path.Combine(instance.RuntimeRoot, "diagnostics"));
        const string restoredIndexes = "SELECT (SELECT count(*) FROM main.sqlite_schema WHERE name IN ('operations_parent_read','operations_kind_read')) " +
            "+ (SELECT count(*) FROM records.sqlite_schema WHERE name='account_run_records_ended_read');";
        try
        {
            using var copy = DatabaseConnections.Open(Path.Combine(inspection.DiagnosticCopyRoot, "runtime.db"), readOnly: true, domains: DatabaseDomain.All);
            Assert.Equal(StateDatabase.SchemaDigest, DatabaseSchema.VerifyCatalog(copy));
            Assert.Equal(3L, DatabaseConnections.Scalar(copy, restoredIndexes));
            Assert.Equal(operation.OperationId.ToString("D"), DatabaseConnections.Scalar(copy, "SELECT operation_id FROM main.operations;"));
            Assert.Equal(latest.FinalAttempt.RunId.ToString("D"),
                DatabaseConnections.Scalar(copy, "SELECT run_id FROM records.run_records ORDER BY ended_at_utc_ticks DESC LIMIT 1;"));
            Assert.Equal(3L, DatabaseConnections.Scalar(copy, "SELECT COUNT(*) FROM records.run_records;"));
        }
        finally { inspection.OwnedCopy!.Discard(); }
        Assert.Equal(before, File.ReadAllBytes(database.DatabasePath));
        Assert.Equal(recordsBefore, File.ReadAllBytes(recordsPath));
        Assert.False(Directory.Exists(inspection.DiagnosticCopyRoot));
        using var lease = InstanceLease.Acquire(instance, new());
        var reopened = StateDatabase.OpenExisting(instance, Path.Combine(instance.RuntimeRoot, "reopen"), new JournalStartupAdmission(instance, new()), lease);
        var durable = new OperationQueryService(instance, lease, reopened).Read(operation.OperationId, "fixture-inspection");
        Assert.NotNull(durable);
        Assert.Equal(operation.OperationId, durable.OperationId);
        Assert.Equal("fixture.command", durable.Kind);
        Assert.Equal("accepted", durable.Status);
        var records = new RunRecordStore(reopened);
        var first = records.ReadPage(new RecordQuery(Limit: 2));
        Assert.Equal(new[] { older, middle }, first.Items);
        Assert.NotNull(first.Next);
        var last = records.ReadPage(new RecordQuery(Limit: 2, After: first.Next));
        Assert.Equal(latest, Assert.Single(last.Items));
        Assert.Null(last.Next);
        using var current = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records);
        Assert.Equal(3L, DatabaseConnections.Scalar(current, restoredIndexes));
    }

    [Theory(DisplayName = "Unknown Or Altered Read Indexes Fail Closed And Preserve Original Bytes")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RejectsAlteredIndexes(int kind)
    {
        var instance = CreateInstance(); var database = StateDatabase.CreateFresh(instance);
        var recordsPath = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "records.db");
        using (var connection = DatabaseConnections.Open(database.DatabasePath, domains: DatabaseDomain.Runtime | DatabaseDomain.Records))
        {
            using var command = connection.CreateCommand();
            command.CommandText = kind switch
            {
                0 => "CREATE INDEX unknown_read ON operations(subject_id);",
                1 => "DROP INDEX operations_parent_read; CREATE UNIQUE INDEX operations_parent_read ON operations(initial_journal_id);",
                _ => "DROP INDEX records.account_run_records_ended_read; CREATE INDEX records.account_run_records_ended_read ON account_run_records(user_id);"
            };
            command.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(database.DatabasePath);
        var recordsBefore = File.ReadAllBytes(recordsPath);
        Assert.Equal("unsupported_database_catalog", Assert.Throws<IOException>(() => DatabaseInspector.Inspect(database.DatabasePath,
            instance.InstanceId, Path.Combine(instance.RuntimeRoot, "diagnostics"))).Message);
        Assert.Equal(before, File.ReadAllBytes(database.DatabasePath));
        Assert.Equal(recordsBefore, File.ReadAllBytes(recordsPath));
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
