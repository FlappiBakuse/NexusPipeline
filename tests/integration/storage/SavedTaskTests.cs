using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class SavedTaskTests
{
    [Theory(DisplayName = "A Saved Task Test Uses The Real Frozen Credential And Socket Without Creating An Execution Receipt")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsesFrozenCredential(bool enabled)
    {
        await using var sink = new NotificationHttpSink((_, _) => Task.FromResult(new NotificationSinkReply(200, "")));
        await using var fixture = new SavedTaskFixture();
        await fixture.Save(new(sink.Address, "original"), enabled);
        var operation = Guid.NewGuid();
        var accepted = fixture.Enqueue(operation); var replay = fixture.Enqueue(operation);
        Assert.True(accepted.IsNew); Assert.False(replay.IsNew);
        Assert.Equal(accepted.Source, replay.Source); Assert.Equal(accepted.Delivery, replay.Delivery);
        var source = fixture.Base.Source;
        var delivery = new NotificationDeliveryEvidence(source.Instance, source.Database, new()).Read(accepted.Delivery);
        Assert.Equal("savedTaskTest", delivery.Document.Source.Kind);
        Assert.Null(delivery.Document.Source.Execution);
        Assert.Equal(0, fixture.Base.Count("attempt_receipts"));
        Assert.Equal(0, fixture.Base.Count("run_records"));
        using (var replacement = new SecretValue(new Uri(sink.Address, "replacement").AbsoluteUri))
            _ = await fixture.Catalog.SaveAsync(Guid.NewGuid(), "local:saved-test", 1, fixture.TaskId, 1, fixture.Input,
                [new(NotificationSecretField.WebhookUrl, 1, SecretDisposition.Set, replacement)]);
        var original = Assert.Single(delivery.Document.Secrets.Where(item => item.Payload is not null)).Payload!;
        Assert.True(DeliveryReferenceIndex.IsSecretReferenced(source.Instance, source.Database, new(),
            original.SecretId, original.Digest, original.SizeBytes));
        Assert.Equal(NotificationDeliveryState.Succeeded, await fixture.Send(accepted));
        Assert.Equal("/original", Assert.Single(sink.Requests).Target);
        Assert.False(DeliveryReferenceIndex.IsSecretReferenced(source.Instance, source.Database, new(),
            original.SecretId, original.Digest, original.SizeBytes));
        Assert.Equal(0, fixture.Base.Count("attempt_receipts"));
    }

    [Fact(DisplayName = "A Saved Task Test Sql Rollback Consumes Neither Source Nor Delivery And The Same Operation Can Retry")]
    public async Task RevertsSavedFailure()
    {
        await using var sink = new NotificationHttpSink((_, _) => Task.FromResult(new NotificationSinkReply(200, "")));
        await using var fixture = new SavedTaskFixture();
        await fixture.Save(new(sink.Address, "saved"));
        var operation = Guid.NewGuid(); var operations = fixture.Base.Count("operations");
        fixture.Base.Execute("CREATE TRIGGER notifications.fail_saved_delivery BEFORE INSERT ON notification_deliveries BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        try { Assert.Throws<SqliteException>(() => fixture.Enqueue(operation)); }
        finally { fixture.Base.Execute("DROP TRIGGER notifications.fail_saved_delivery;"); }
        Assert.Equal(operations, fixture.Base.Count("operations"));
        Assert.Equal(0, fixture.Base.Count("notification_saved_tests"));
        Assert.Equal(0, fixture.Base.Count("notification_deliveries"));
        Assert.Empty(sink.Requests);
        var accepted = fixture.Enqueue(operation);
        Assert.Equal(NotificationDeliveryState.Succeeded, await fixture.Send(accepted));
        Assert.Single(sink.Requests);
    }

    [Theory(DisplayName = "A Conflicting Saved Test Identity Or Sql Source Cannot Acquire Sending Authority")]
    [InlineData("subject")]
    [InlineData("source-membership")]
    public async Task RejectsConflictingSource(string damage)
    {
        await using var sink = new NotificationHttpSink((_, _) => Task.FromResult(new NotificationSinkReply(200, "")));
        await using var fixture = new SavedTaskFixture();
        await fixture.Save(new(sink.Address, "saved"));
        var operation = Guid.NewGuid(); var accepted = fixture.Enqueue(operation);
        var before = fixture.Base.Count("operations");
        if (damage == "subject")
        {
            Assert.Throws<OperationConflictException>(() => fixture.Enqueue(operation, "remote:other-subject"));
            Assert.Equal(before, fixture.Base.Count("operations"));
        }
        else
        {
            var source = fixture.Base.Source;
            var evidence = new NotificationDeliveryEvidence(source.Instance, source.Database, new());
            Assert.Equal(accepted.Delivery, evidence.Read(accepted.Delivery).Reference);
            using (var connection = DatabaseConnections.Open(source.Database.DatabasePath, domains: DatabaseDomain.Notifications))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE notifications.notification_saved_tests SET sha256=$changed;";
                command.Parameters.AddWithValue("$changed", new string('0', 64));
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            var beforeGroup = RuntimeFingerprint.CaptureDatabaseFiles(source.Database.DatabasePath, DatabaseDomain.All);
            Assert.Equal("notification_saved_test_unbound", Assert.Throws<IOException>(() => evidence.Read(accepted.Delivery)).Message);
            Assert.Equal(beforeGroup, RuntimeFingerprint.CaptureDatabaseFiles(source.Database.DatabasePath, DatabaseDomain.All));
            Assert.Equal(before, fixture.Base.Count("operations"));
        }
        Assert.Empty(sink.Requests);
        Assert.Equal(0, fixture.Base.Count("attempt_receipts"));
    }
}
