using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ReceiptFanoutTests
{
    [Theory(DisplayName = "Multiple Saved Targets Receive Distinct Deliveries From One Actual Queue Or Each Frozen Binding")]
    [InlineData(true, 1, 2)]
    [InlineData(false, 3, 6)]
    public async Task ScopesSavedTargets(
        bool queueNotifications, int expectedRoutes, int expectedDeliveries)
    {
        using var fixture = new NotificationReceiptFixture();
        await fixture.Execute(taskCount: 2, queueNotifications: queueNotifications);
        Assert.Equal(expectedRoutes, fixture.Snapshot.Document.RouteCount);
        Assert.Equal(2, fixture.Snapshot.Document.Tasks.Length);
        var materialized = 0;
        while (true)
        {
            var count = fixture.Delivery.MaterializePending(2);
            if (count == 0) break;
            materialized += count;
            Assert.InRange(materialized, 1, expectedRoutes);
        }
        Assert.Equal(expectedRoutes, materialized);
        var status = fixture.Delivery.ReadPendingForRun(fixture.Receipt.Facts.RunId);
        Assert.True(status.IsExpanded); Assert.False(status.IsSettled);
        Assert.Equal(expectedDeliveries, status.Queued);
        using var connection = DatabaseConnections.Open(fixture.Source.Database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT operation_id,acceptance_id,task_id,relative_path,size_bytes,sha256 FROM notification_deliveries ORDER BY operation_id;";
        var delivered = new Dictionary<Guid, int>(); var ids = new HashSet<Guid>();
        using (var rows = command.ExecuteReader())
            while (rows.Read())
            {
                var id = Guid.ParseExact(rows.GetString(0), "D");
                Assert.True(ids.Add(id));
                Assert.Equal(fixture.Snapshot.Document.OperationId.ToString("D"), rows.GetString(1));
                var task = Guid.ParseExact(rows.GetString(2), "D");
                Assert.Contains(task, fixture.TaskIds);
                delivered[task] = delivered.GetValueOrDefault(task) + 1;
                var snapshot = new NotificationDeliveryEvidence(fixture.Source.Instance, fixture.Source.Database, new())
                    .ReadFromSnapshot(new(rows.GetString(3), rows.GetInt64(4), rows.GetString(5)), connection, transaction);
                Assert.Equal(id, snapshot.Document.OperationId);
                Assert.Equal(task, snapshot.Document.Task.TaskId);
            }
        Assert.Equal(expectedDeliveries, ids.Count);
        Assert.Equal(2, delivered.Count); Assert.All(delivered.Values, count => Assert.Equal(expectedRoutes, count));
        transaction.Commit();
        fixture.Registration.OnCommitted(fixture.Receipt);
        Assert.Equal(0, fixture.Delivery.MaterializePending(2));
        Assert.Equal(expectedDeliveries, fixture.Count("notification_deliveries"));
    }
}
