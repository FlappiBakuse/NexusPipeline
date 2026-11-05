using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Notifications;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class NotificationReceiptTests
{
    [Theory(DisplayName = "Three Queue Occurrences Publish One Notification Route Per Binding And Remain Startup Reconcilable")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoutesRepeatedBindings(bool queueNotifications)
    {
        using var fixture = new NotificationReceiptFixture();
        await fixture.Execute(queueNotifications: queueNotifications, repetitions: 3);
        var report = new NexusPipeline.Platform.Storage.Execution.ExecutionLogReader(fixture.Source.Instance,
            fixture.Source.Database, new()).ReadReport(fixture.Receipt.Facts.RunId, fixture.Receipt.Facts.AttemptId);
        Assert.Equal(9, report.Steps.Count);
        Assert.All(report.Steps, step => Assert.Equal("collected", step.Phase));
        Assert.Equal(queueNotifications ? 1 : 3, fixture.Delivery.MaterializePending());
        Assert.Equal(queueNotifications ? 1 : 3, fixture.Count("notification_deliveries"));
        fixture.Registration.ReconcilePending();
        fixture.Registration.OnCommitted(fixture.Receipt);
        Assert.Equal(1, fixture.Count("notification_receipt_sources"));
        Assert.True(fixture.Base.Index.Current.IsReady);
    }

    [Theory(DisplayName = "Actual Delivery Uses The Saved Receipt Locale Even If Settings Change Before Fanout")]
    [InlineData(NexusPipeline.Core.Settings.HostLocale.ZhCn)]
    [InlineData(NexusPipeline.Core.Settings.HostLocale.EnUs)]
    public async Task KeepsSavedLocale(NexusPipeline.Core.Settings.HostLocale locale)
    {
        using var fixture = new NotificationReceiptFixture();
        Save(locale); await fixture.Execute();
        Save(locale == NexusPipeline.Core.Settings.HostLocale.ZhCn ? NexusPipeline.Core.Settings.HostLocale.EnUs : NexusPipeline.Core.Settings.HostLocale.ZhCn);
        Assert.Equal(1, fixture.Delivery.MaterializePending());
        using var connection = NexusPipeline.Platform.Storage.Sqlite.DatabaseConnections.Open(fixture.Source.Database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT relative_path,size_bytes,sha256 FROM notification_deliveries;";
        using var row = command.ExecuteReader(); Assert.True(row.Read());
        var delivery = new NotificationDeliveryCodec(fixture.Source.Instance, new()).Read(new(row.GetString(0), row.GetInt64(1), row.GetString(2))).Document;
        Assert.Contains(locale == NexusPipeline.Core.Settings.HostLocale.ZhCn ? "执行：" : "Execution:", delivery.Body);
        Assert.Contains("Engine=", delivery.Body); Assert.Contains("Reason=", delivery.Body);
        Assert.Equal(locale, fixture.Snapshot.Document.Locale);

        void Save(NexusPipeline.Core.Settings.HostLocale value)
        {
            var saved = fixture.Source.Settings.Read();
            var change = new NexusPipeline.Core.Settings.HostSettingsChange(saved.Revision, saved.Values with { Locale = value });
            fixture.Source.Settings.Commit(NexusPipeline.Platform.Storage.Sqlite.HostSettingsStore.Identity(Guid.NewGuid(), "locale-fixture", change), change, DateTimeOffset.UtcNow);
        }
    }

    [Fact(DisplayName = "An Actual Receipt Pins Its Frozen Credential Across Replacement And Route Publication")]
    public async Task PinsFrozenCredential()
    {
        using var fixture = new NotificationReceiptFixture(); await fixture.Execute();
        var source = fixture.Snapshot.Document;
        var original = Assert.Single(Assert.Single(source.Tasks).Secrets.Where(item => item.Payload is not null)).Payload!;
        Assert.False(fixture.Delivery.IsSettledForRun(fixture.Receipt.Facts.RunId));
        await fixture.Replace();
        Assert.True(DeliveryReferenceIndex.IsSecretReferenced(fixture.Source.Instance, fixture.Source.Database,
            new(), original.SecretId, original.Digest, original.SizeBytes));
        Assert.Equal(1, fixture.Delivery.MaterializePending());
        Assert.Equal(0, fixture.Delivery.MaterializePending());
        fixture.Registration.OnCommitted(fixture.Receipt);
        Assert.Equal(1, fixture.Count("notification_receipt_sources"));
        Assert.Equal(1, fixture.Count("notification_deliveries"));
        var status = fixture.Delivery.ReadPendingForRun(fixture.Receipt.Facts.RunId);
        Assert.True(status.IsRegistered); Assert.True(status.IsExpanded); Assert.Equal(1, status.Queued); Assert.False(status.IsSettled);
        Assert.True(DeliveryReferenceIndex.IsSecretReferenced(fixture.Source.Instance, fixture.Source.Database,
            new(), original.SecretId, original.Digest, original.SizeBytes));
        Assert.Null(source.Screenshot);
    }

    [Fact(DisplayName = "An Enabled Empty Queue Publishes An Empty Authoritative Route Without Binding Fallback")]
    public async Task PublishesEmptyRoutes()
    {
        using var fixture = new NotificationReceiptFixture(); await fixture.Execute(emptyQueue: true);
        Assert.Empty(fixture.Snapshot.Document.Tasks);
        Assert.Equal(1, fixture.Delivery.MaterializePending());
        Assert.Equal(0, fixture.Count("notification_deliveries"));
        var status = fixture.Delivery.ReadPendingForRun(fixture.Receipt.Facts.RunId);
        Assert.True(status.IsRegistered); Assert.True(status.IsExpanded); Assert.True(status.IsSettled);
        Assert.Equal(0, status.Queued);
    }

    [Theory(DisplayName = "A Route Sql Failure Preserves The Source Pin And Retries The Same Immutable Delivery")]
    [InlineData("notification_receipt_routes")]
    [InlineData("notification_deliveries")]
    public async Task ReplaysFailedRoutes(string table)
    {
        using var fixture = new NotificationReceiptFixture(); await fixture.Execute();
        fixture.Sql($"CREATE TRIGGER fail_route BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        Assert.Throws<SqliteException>(() => fixture.Delivery.MaterializePending());
        Assert.Equal(0, fixture.Count("notification_receipt_routes")); Assert.Equal(0, fixture.Count("notification_deliveries"));
        Assert.Equal(1, fixture.Count("notification_receipt_sources"));
        var payload = Assert.Single(Assert.Single(fixture.Snapshot.Document.Tasks).Secrets.Where(item => item.Payload is not null)).Payload!;
        Assert.True(DeliveryReferenceIndex.IsSecretReferenced(fixture.Source.Instance, fixture.Source.Database,
            new(), payload.SecretId, payload.Digest, payload.SizeBytes));
        fixture.Sql("DROP TRIGGER fail_route;");
        Assert.Equal(1, fixture.Delivery.MaterializePending()); Assert.Equal(0, fixture.Delivery.MaterializePending());
        Assert.Equal(1, fixture.Count("notification_deliveries"));
    }

    [Fact(DisplayName = "Source And Delivery Reference Handoff Uses One Sql Read Snapshot")]
    public async Task ReadsCoherentHandoff()
    {
        using var fixture = new NotificationReceiptFixture(); await fixture.Execute(); await fixture.Replace();
        var sourceCalls = 0; var deliveryCalls = 0; var sourceHeld = false;
        NotificationReferenceInventory.Visit(fixture.Source.Instance, fixture.Source.Database, new(), (source, expanded, _) =>
        {
            sourceCalls++; Assert.False(expanded); sourceHeld = source.Snapshot.Document.Tasks.Any(task => task.Secrets.Any(item => item.Payload is not null));
            Assert.Equal(1, fixture.Delivery.MaterializePending());
        }, (_, _) => deliveryCalls++);
        Assert.Equal(1, sourceCalls); Assert.True(sourceHeld); Assert.Equal(0, deliveryCalls);
        var payload = Assert.Single(Assert.Single(fixture.Snapshot.Document.Tasks).Secrets.Where(item => item.Payload is not null)).Payload!;
        Assert.True(DeliveryReferenceIndex.IsSecretReferenced(fixture.Source.Instance, fixture.Source.Database,
            new(), payload.SecretId, payload.Digest, payload.SizeBytes));
    }

    [Fact(DisplayName = "Damaged Source Metadata Is Rejected Before Its Old Credential Can Be Retired")]
    public async Task RejectsDamagedSources()
    {
        using var fixture = new NotificationReceiptFixture(); await fixture.Execute();
        var before = fixture.Count("operations");
        fixture.Sql("UPDATE notification_receipt_sources SET route_count=route_count+1;");
        var payload = Assert.Single(Assert.Single(fixture.Snapshot.Document.Tasks).Secrets.Where(item => item.Payload is not null)).Payload!;
        Assert.ThrowsAny<Exception>(() => DeliveryReferenceIndex.IsSecretReferenced(fixture.Source.Instance,
            fixture.Source.Database, new(), payload.SecretId, payload.Digest, payload.SizeBytes));
        Assert.ThrowsAny<Exception>(() => fixture.Delivery.ReadPendingForRun(fixture.Receipt.Facts.RunId));
        Assert.Equal(before, fixture.Count("operations"));
    }
}
