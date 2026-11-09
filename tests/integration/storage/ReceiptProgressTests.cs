using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage.Notifications;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ReceiptProgressTests
{
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
