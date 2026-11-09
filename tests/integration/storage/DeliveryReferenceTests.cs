using System.Runtime.Versioning;
using System.Text;
using NexusPipeline.Core.Records;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class DeliveryReferenceTests
{
    [Theory(DisplayName = "Queued Or Unknown Delivery Metadata Pins The Exact Old Dpapi Payload After Catalog Replacement")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PinsHistoricalCredentials(bool unknown)
    {
        using var fixture = new DeliveryReferenceFixture(); await fixture.Prepare();
        fixture.Accept(); fixture.Accept();
        if (unknown)
        {
            fixture.Transition(NotificationDeliveryState.Sending);
            fixture.Transition(NotificationDeliveryState.Unknown);
        }
        await fixture.Replace();
        var old = Assert.Single(fixture.Snapshot.Document.Secrets.Where(field => field.Payload is not null)).Payload!;
        var blobs = new ImmutableBlobStore(fixture.Source.Instance, new());
        var candidates = Enumerable.Range(0, 31).Select(index => blobs.PersistContent(BitConverter.GetBytes(index), default)).ToHashSet();
        var credential = new BlobReference($"secrets/payloads/{old.SecretId:D}-{old.Digest}.bin", old.SizeBytes, old.Digest);
        candidates.Add(credential);
        var index = new BlobReferenceIndex(fixture.Source.Instance, new(), fixture.Source.Database, blobs);
        Assert.Equal(credential, Assert.Single(index.ReadReferenced(candidates)));
        var read = new NotificationDeliveryEvidence(fixture.Source.Instance, fixture.Source.Database, new()).Read(fixture.Snapshot.Reference);
        Assert.Equal(fixture.Snapshot.Reference, read.Reference);
        Assert.Equal(NotificationCatalogCodec.Encode(fixture.Snapshot.Document), NotificationCatalogCodec.Encode(read.Document));
        using var connection = DatabaseConnections.Open(fixture.Source.Database.DatabasePath, readOnly: true);
        var current = SecretFieldRows.ReadField(connection, null,
            SecretFieldKey.NotificationTask(fixture.Source.Instance.InstanceId, fixture.TaskId, NotificationSecretField.WebhookUrl));
        Assert.NotEqual(old.SecretId, current.SecretId);
        Assert.Equal(unknown ? NotificationDeliveryState.Unknown : NotificationDeliveryState.Queued,
            NotificationDeliveryRows.ReadState(connection, null, fixture.Snapshot.Document.OperationId).State);
    }

    [Theory(DisplayName = "Stale Credential Or An Unpublished Attachment Consumes No Delivery Operation Or Reference")]
    [InlineData("secret-cas")]
    [InlineData("unpublished-attachment")]
    public async Task RejectsUnpublishedInputs(string conflict)
    {
        using var fixture = new DeliveryReferenceFixture(); await fixture.Prepare();
        var snapshot = fixture.Snapshot;
        if (conflict == "secret-cas") await fixture.Replace();
        else
        {
            var bytes = new ImmutableBlobStore(fixture.Source.Instance, new()).PersistContent(Encoding.UTF8.GetBytes("unattached"), default);
            snapshot = new NotificationDeliveryCodec(fixture.Source.Instance, new()).Persist(snapshot.Document with
            {
                Attachment = new RecordAsset(snapshot.Document.Source.Receipt.Facts.RunId, bytes.Sha256, bytes.SizeBytes, RecordAssetKind.Screenshot)
            });
        }
        var before = fixture.Operations();
        Assert.ThrowsAny<Exception>(() => fixture.Accept(snapshot));
        Assert.Equal(before, fixture.Operations());
        using var connection = DatabaseConnections.Open(fixture.Source.Database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT count(*) FROM notification_deliveries;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact(DisplayName = "Unknown Historical Transition Bytes Are Rejected Before Gc Can Release A Credential Reference")]
    public async Task RejectsUnknownTransitions()
    {
        using var fixture = new DeliveryReferenceFixture(); await fixture.Prepare(); fixture.Accept();
        using (var connection = DatabaseConnections.Open(fixture.Source.Database.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE notification_delivery_transitions SET document_json=json_set(document_json,'$.unexpected',true);";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var old = Assert.Single(fixture.Snapshot.Document.Secrets.Where(field => field.Payload is not null)).Payload!;
        Assert.ThrowsAny<Exception>(() => DeliveryReferenceIndex.IsSecretReferenced(fixture.Source.Instance,
            fixture.Source.Database, new(), old.SecretId, old.Digest, old.SizeBytes));
    }
}
