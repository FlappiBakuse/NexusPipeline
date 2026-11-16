using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class DatabaseSnapshotTests
{
    [Fact(DisplayName = "A Records Then Runtime Snapshot Finishes During A Cross Database Commit Without Repeating Either Callback")]
    public async Task OrdersCrossDomainSnapshot()
    {
        var (database, records) = RecordStoreFixture.NewStore();
        var record = RecordStoreFixture.Record(0, Guid.NewGuid());
        var operation = new OperationIdentity(Guid.NewGuid(), "snapshot-fixture", new string('a', 64), "fixture.snapshot");
        var sequence = database.CommitSequence;
        using var pending = new ManualResetEventSlim();
        BusyCallback busy = (_, _) => { pending.Set(); return 0; };
        Task<bool>? writer = null;
        var reads = 0; var writes = 0;
        try
        {
            HistoryReadScope.ReadDatabase(database, (connection, transaction) =>
            {
                reads++;
                Assert.Equal(0L, Count(connection, transaction, "records.run_records"));
                writer = CommitBoth(database, operation, record, busy, () => writes++);
                Assert.True(pending.Wait(TimeSpan.FromSeconds(10)));
                Assert.Equal(0L, Count(connection, transaction, "main.operations"));
                Assert.Equal(0L, Count(connection, transaction, "records.run_records"));
                return true;
            }, DatabaseDomain.Records);
        }
        finally
        {
            if (writer is not null) Assert.True(await writer.WaitAsync(TimeSpan.FromSeconds(10)));
            GC.KeepAlive(busy);
        }
        Assert.Equal(1, reads); Assert.Equal(1, writes);
        Assert.Equal(sequence + 1, database.CommitSequence);
        Assert.Equal(record, Assert.Single(records.ReadPage(new()).Items));
        Assert.False(database.Accept(operation, DateTimeOffset.UtcNow).IsNew);
    }

    [Fact(DisplayName = "A Busy Snapshot Start Releases Its Transaction And Reopens The Same Connection After Commit")]
    public async Task ReleasesFailedSnapshotStart()
    {
        var (database, records) = RecordStoreFixture.NewStore();
        var record = RecordStoreFixture.Record(0, Guid.NewGuid());
        var operation = new OperationIdentity(Guid.NewGuid(), "snapshot-fixture", new string('b', 64), "fixture.snapshot");
        using var reading = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Records);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Records);
        using var pending = new ManualResetEventSlim();
        BusyCallback busy = (_, _) => { pending.Set(); return 0; };
        Task<bool>? writer = null;
        try
        {
            using (var snapshot = reading.BeginTransaction(deferred: true))
            {
                Assert.Equal(0L, Count(reading, snapshot, "records.run_records"));
                writer = CommitBoth(database, operation, record, busy, () => { });
                Assert.True(pending.Wait(TimeSpan.FromSeconds(10)));
                Assert.Equal(5, Assert.Throws<SqliteException>(() => DatabaseConnections.BeginRead(connection)).SqliteErrorCode);
            }
        }
        finally
        {
            if (writer is not null) Assert.True(await writer.WaitAsync(TimeSpan.FromSeconds(10)));
            GC.KeepAlive(busy);
        }
        using var following = DatabaseConnections.BeginRead(connection);
        Assert.Equal(1L, Count(connection, following, "main.operations"));
        Assert.Equal(1L, Count(connection, following, "records.run_records"));
        following.Commit();
        Assert.Equal(record, Assert.Single(records.ReadPage(new()).Items));
    }

    [Fact(DisplayName = "A Transient History Reader Delays Commit Without Repeating The Save Or Its Published Record")]
    public async Task WaitsForTransientSnapshot()
    {
        var (database, records) = RecordStoreFixture.NewStore();
        var record = RecordStoreFixture.Record(0, Guid.NewGuid());
        var sequence = database.CommitSequence;
        var pinned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writing = new ManualResetEventSlim();
        var reader = Task.Run(() =>
        {
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Records);
            using var snapshot = connection.BeginTransaction(deferred: true);
            using var query = connection.CreateCommand(); query.Transaction = snapshot;
            query.CommandText = "SELECT count(*) FROM records.run_records;";
            Assert.Equal(0L, query.ExecuteScalar());
            pinned.SetResult();
            Assert.True(writing.Wait(TimeSpan.FromSeconds(10)));
            Thread.Sleep(TimeSpan.FromMilliseconds(2500));
            snapshot.Commit();
        });
        var calls = 0;
        try
        {
            await pinned.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(database.Write((connection, transaction) =>
            {
                calls++;
                RunRecordStore.PublishInside(connection, transaction, record);
                writing.Set();
                return true;
            }));
        }
        finally { writing.Set(); await reader.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Equal(1, calls);
        Assert.Equal(sequence + 1, database.CommitSequence);
        Assert.Equal(record, Assert.Single(records.ReadPage(new()).Items));
    }

    [Fact(DisplayName = "A Returned History Page Releases Its Snapshot Before The Next Atomic Save")]
    public void ReleasesReadSnapshots()
    {
        var (database, records) = RecordStoreFixture.NewStore();
        var automation = Guid.NewGuid();
        var original = RecordStoreFixture.Record(0, automation);
        records.AppendCompleted([original]);
        var first = records.ReadPage(new(50, automation));
        Assert.Equal(original, Assert.Single(first.Items));
        var added = Enumerable.Range(1, 200).Select(number => RecordStoreFixture.Record(number, automation) with
            { EndedAt = original.EndedAt.AddSeconds(number) }).ToArray();
        records.AppendCompleted(added);
        var query = new RecordQuery(50, automation);
        var expected = added.Prepend(original).Take(query.Limit).ToArray();
        var reopened = new RunRecordStore(database);
        var page = reopened.ReadPage(query);
        Assert.Equal(expected, page.Items);
        Assert.Equal(new RecordCursor(expected[^1].EndedAt, expected[^1].FinalAttempt.RunId), page.Next);
        Assert.Equal(original, Assert.Single(first.Items));
        Assert.Equal(added[^1], Assert.Single(reopened.ReadPage(query with { EndedFromInclusive = added[^1].EndedAt }).Items));
    }

    [Fact(DisplayName = "A Busy History Reader Cannot Leave A Partially Committed Save")]
    public void PreservesPinnedSnapshot()
    {
        var (database, records) = RecordStoreFixture.NewStore();
        var automation = Guid.NewGuid();
        var original = RecordStoreFixture.Record(0, automation);
        var added = RecordStoreFixture.Record(1, automation);
        records.AppendCompleted([original]);
        using (var reading = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Records))
        using (var snapshot = reading.BeginTransaction(deferred: true))
        {
            using var query = reading.CreateCommand(); query.Transaction = snapshot;
            query.CommandText = "SELECT count(*) FROM records.run_records;";
            Assert.Equal(1L, query.ExecuteScalar());
            Assert.Equal(5, Assert.Throws<SqliteException>(() => records.AppendCompleted([added])).SqliteErrorCode);
            Assert.Equal(1L, query.ExecuteScalar());
            snapshot.Commit();
        }
        Assert.Equal(original, Assert.Single(records.ReadPage(new(50, automation)).Items));
        records.AppendCompleted([added]);
        Assert.Equal(2, records.ReadPage(new(50, automation)).Items.Count);
    }

    private static long Count(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM " + table + ";";
        return (long)command.ExecuteScalar()!;
    }

    private static Task<bool> CommitBoth(StateDatabase database, OperationIdentity operation,
        CompletedRunRecord record, BusyCallback busy, Action write) => Task.Run(() =>
    {
        using var scope = new DatabaseWriteScope(database.DatabasePath);
        var connection = DatabaseWriteScope.Borrow(database.DatabasePath)!;
        try
        {
            return database.Write((sql, transaction) =>
            {
                write();
                OperationRows.Accept(sql, transaction, operation, DateTimeOffset.UtcNow);
                RunRecordStore.PublishInside(sql, transaction, record);
                Assert.Equal(0, BusyHandler(sql.Handle!.DangerousGetHandle(), busy, IntPtr.Zero));
                return true;
            });
        }
        finally { BusyHandler(connection.Handle!.DangerousGetHandle(), null, IntPtr.Zero); }
    });

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BusyCallback(IntPtr user, int count);

    [DllImport("e_sqlite3", EntryPoint = "sqlite3_busy_handler", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BusyHandler(IntPtr database, BusyCallback? callback, IntPtr user);
}
