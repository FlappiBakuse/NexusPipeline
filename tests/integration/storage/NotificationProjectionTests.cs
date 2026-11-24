using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class NotificationProjectionTests(ITestOutputHelper output)
{
    [Theory(DisplayName = "A Warm Notification Release Verifies Changed Rows Without Rescanning Settled History")]
    [InlineData(128)]
    [InlineData(2048)]
    [InlineData(12000)]
    public void BoundsSettledHistoryProof(int history)
    {
        using var fixture = new ExecutionAdmissionFixture();
        var source = fixture.Source;
        source.Lease.BindDatabase(source.Database);
        var path = source.Database.DatabasePath;
        const DatabaseDomain domains = DatabaseDomain.Runtime | DatabaseDomain.Notifications;
        var last = $"fa7293c7-28de-41ef-b83b-{history:D12}";
        const string orphan = "fa7293c7-28de-41ef-b83b-000000012001";
        using (var writer = DatabaseConnections.Open(path))
        using (var transaction = writer.BeginTransaction(deferred: false))
        {
            using var seed = DatabaseConnections.Command(writer, transaction, """
                WITH RECURSIVE numbers(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM numbers WHERE n<$0)
                INSERT INTO main.operations(operation_id,subject_id,request_digest,operation_kind,status,created_at)
                SELECT printf('fa7293c7-28de-41ef-b83b-%012d',n),'fixture:notification-history',printf('%064d',n),
                       'notification.delivery','completed','2026-01-01T00:00:00Z' FROM numbers;
                INSERT INTO notifications.notification_deliveries
                SELECT operation_id,operation_id,NULL,'fa7293c7-28de-41ef-b83c-000000000001','blobs/plans/fixture.json',1,request_digest
                FROM main.operations WHERE subject_id='fixture:notification-history';
                INSERT INTO notifications.notification_delivery_transitions
                SELECT operation_id,0,'{"state":"succeeded"}' FROM notifications.notification_deliveries
                WHERE operation_id LIKE 'fa7293c7-28de-41ef-b83b-%';
                """, history);
            seed.ExecuteNonQuery();
            transaction.Commit();
        }
        var queue = new NotificationRuntimeQueue(source.Instance, source.Database, new());
        Assert.True(queue.VerifySettled());
        var unchanged = CountInstructions(expectIncremental: true);
        const string update = "UPDATE notifications.notification_delivery_transitions SET document_json=$0 WHERE operation_id=$1;";
        Change(update, "{\"state\":\"failed\"}", last);
        var changed = CountInstructions(expectIncremental: true);
        using (var other = DatabaseConnections.Open(path, allowPooling: false, domains: domains))
        using (var restore = DatabaseConnections.Command(other, null, update, "{\"state\":\"succeeded\"}", last))
            Assert.Equal(1, restore.ExecuteNonQuery());
        var full = CountInstructions(expectIncremental: false);

        Change(update, "{\"state\":\"unknown\"}", last);
        Assert.Throws<IOException>(() => queue.VerifySettled());
        Change(update, "{\"state\":\"succeeded\"}", last);
        Assert.True(queue.VerifySettled());

        Change("""
            INSERT INTO main.operations(operation_id,subject_id,request_digest,operation_kind,status,created_at)
            VALUES($0,'fixture:orphan',printf('%064d',0),'notification.delivery','completed','2026-01-01T00:00:00Z');
            """, orphan);
        Assert.Throws<IOException>(() => queue.VerifySettled());
        Change("DELETE FROM main.operations WHERE operation_id=$0;", orphan);
        Assert.True(queue.VerifySettled());

        using (var other = DatabaseConnections.Open(path, allowPooling: false, domains: domains))
        using (var remove = DatabaseConnections.Command(other, null,
            "DELETE FROM notifications.notification_delivery_transitions WHERE operation_id=$0;", last))
            Assert.Equal(1, remove.ExecuteNonQuery());
        Assert.Throws<IOException>(() => queue.VerifySettled());
        output.WriteLine($"settled notification verification: {history} historical deliveries, SQLite progress callbacks at 100 VM instructions" +
            $" full={full}, unchanged={unchanged}, one changed delivery={changed}");
        Assert.InRange(unchanged, 0, 12);
        Assert.InRange(changed, 1, 12);
        Assert.True(full > changed);

        void Change(string sql, params object[] parameters)
        {
            using var writer = DatabaseConnections.Open(path);
            using var command = DatabaseConnections.Command(writer, null, sql, parameters);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        int CountInstructions(bool expectIncremental)
        {
            var batches = 0;
            bool? incremental = null;
            SQLitePCL.strdelegate_trace trace = (_, sql) =>
            {
                if (sql.Contains("LEFT JOIN notifications.notification_delivery_transitions t", StringComparison.Ordinal))
                    incremental = sql.Contains("temp.notification_projection_changed", StringComparison.Ordinal);
            };
            SQLitePCL.sqlite3 handle;
            using (var connection = DatabaseConnections.Open(path)) handle = connection.Handle!;
            SQLitePCL.raw.sqlite3_progress_handler(handle, 100, _ => { batches++; return 0; }, null);
            SQLitePCL.raw.sqlite3_trace(handle, trace, null);
            try { Assert.True(queue.VerifySettled()); }
            finally
            {
                SQLitePCL.raw.sqlite3_progress_handler(handle, 0, null, null);
                SQLitePCL.raw.sqlite3_trace(handle, (SQLitePCL.strdelegate_trace?)null, null);
            }
            Assert.Equal(expectIncremental, incremental);
            return batches;
        }
    }
}
