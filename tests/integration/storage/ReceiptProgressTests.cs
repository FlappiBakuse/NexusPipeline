using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ReceiptProgressTests
{
    [Fact(DisplayName = "Actual Partial Route Publication Advances Its Cursor With Every Delivery And Uses The Pending Index")]
    public async Task CommitsRouteProgress()
    {
        using var fixture = new NotificationReceiptFixture();
        await fixture.Execute(taskCount: 2, queueNotifications: false);
        Assert.Equal(3, fixture.Snapshot.Document.RouteCount);
        for (var ordinal = 0; ordinal < 3; ordinal++)
        {
            Assert.Equal(1, fixture.Delivery.MaterializePending(1));
            using var connection = DatabaseConnections.Open(fixture.Source.Database.DatabasePath, readOnly: true);
            using var transaction = connection.BeginTransaction(deferred: true);
            Assert.Equal(ordinal + 1, ReceiptProgressRows.Require(connection, transaction,
                fixture.Snapshot.Document.OperationId, 3));
            var status = fixture.Delivery.ReadPendingForRun(fixture.Receipt.Facts.RunId);
            Assert.Equal((ordinal + 1) * 2, status.Queued);
            Assert.Equal(ordinal == 2, status.IsExpanded);
            transaction.Commit();
        }
        Assert.Equal(0, fixture.Delivery.MaterializePending(1));
        using var database = DatabaseConnections.Open(fixture.Source.Database.DatabasePath, readOnly: true);
        using var plan = database.CreateCommand();
        plan.CommandText = "EXPLAIN QUERY PLAN " + NotificationDeliveryService.PendingRoutesSql;
        plan.Parameters.AddWithValue("$0", 64);
        var details = new List<string>();
        using (var rows = plan.ExecuteReader()) while (rows.Read()) details.Add(rows.GetString(3));
        Assert.Contains(details, detail => detail.Contains("ix_notification_receipt_pending", StringComparison.Ordinal));
        Assert.DoesNotContain(details, detail => detail.Contains("RECURSIVE", StringComparison.OrdinalIgnoreCase));
    }

    [Theory(DisplayName = "A Changed Derived Cursor Cannot Skip Routes Or Release The Frozen Credential")]
    [InlineData("missing")]
    [InlineData("unearned")]
    [InlineData("count")]
    public async Task RejectsChangedCursors(string fault)
    {
        using var fixture = new NotificationReceiptFixture(); await fixture.Execute(); await fixture.Replace();
        fixture.Sql(fault switch
        {
            "missing" => "DELETE FROM notification_receipt_progress;",
            "unearned" => "UPDATE notification_receipt_progress SET next_ordinal=1;",
            _ => "UPDATE notification_receipt_progress SET route_count=2;"
        });
        var original = Assert.Single(Assert.Single(fixture.Snapshot.Document.Tasks).Secrets, secret => secret.Payload is not null).Payload!;
        var operations = fixture.Count("operations");
        Assert.ThrowsAny<IOException>(() => fixture.Delivery.ReadPendingForRun(fixture.Receipt.Facts.RunId));
        Assert.ThrowsAny<IOException>(() => DeliveryReferenceIndex.IsSecretReferenced(fixture.Source.Instance,
            fixture.Source.Database, new(), original.SecretId, original.Digest, original.SizeBytes));
        Assert.Equal(operations, fixture.Count("operations"));
        Assert.Equal(0, fixture.Count("notification_receipt_routes"));
        Assert.Equal(0, fixture.Count("notification_deliveries"));
    }
}
