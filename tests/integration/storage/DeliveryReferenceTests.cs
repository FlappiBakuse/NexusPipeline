using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
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
        using var connection = DatabaseConnections.Open(fixture.Source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.All);
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
        var codec = new NotificationDeliveryCodec(fixture.Source.Instance, new());
        if (conflict == "secret-cas")
        {
            await fixture.Replace();
            using var catalog = DatabaseConnections.Open(fixture.Source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Notifications);
            var current = NotificationCatalogRows.ReadCurrent(catalog, null)!;
            var task = Assert.Single(new NotificationCatalogCodec(fixture.Source.Instance, new()).Read(current.Reference).Document.Tasks);
            snapshot = codec.Persist(snapshot.Document with { Catalog = current.Reference, CatalogRevision = current.Revision, Task = task });
        }
        else
        {
            var bytes = new ImmutableBlobStore(fixture.Source.Instance, new()).PersistContent(Encoding.UTF8.GetBytes("unattached"), default);
            snapshot = codec.Persist(snapshot.Document with
            {
                Attachment = new RecordAsset(snapshot.Document.Source.Receipt.Facts.RunId, bytes.Sha256, bytes.SizeBytes, RecordAssetKind.Screenshot)
            });
        }
        var before = fixture.Operations();
        var evidence = fixture.Source.Snapshot();
        if (conflict == "secret-cas")
            Assert.Equal("secret_revision_conflict", Assert.Throws<InvalidOperationException>(() => fixture.Accept(snapshot)).Message);
        else
            Assert.Equal("notification_delivery_attachment_unbound", Assert.Throws<IOException>(() => fixture.Accept(snapshot)).Message);
        Assert.Equal(before, fixture.Operations());
        var after = fixture.Source.Snapshot(); Assert.Equal(evidence.Rows, after.Rows); Assert.Equal(evidence.Files, after.Files);
        using var connection = DatabaseConnections.Open(fixture.Source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT count(*) FROM notifications.notification_deliveries;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact(DisplayName = "Unknown Historical Transition Bytes Are Rejected Before Gc Can Release A Credential Reference")]
    public async Task RejectsUnknownTransitions()
    {
        using var fixture = new DeliveryReferenceFixture(); await fixture.Prepare(); fixture.Accept();
        var old = Assert.Single(fixture.Snapshot.Document.Secrets.Where(field => field.Payload is not null)).Payload!;
        var payload = Path.Combine(fixture.Source.Instance.RuntimeRoot, "secrets", "payloads", $"{old.SecretId:D}-{old.Digest}.bin");
        var original = File.ReadAllBytes(payload);
        Assert.True(DeliveryReferenceIndex.IsSecretReferenced(fixture.Source.Instance, fixture.Source.Database, new(), old.SecretId, old.Digest, old.SizeBytes));
        using (var connection = DatabaseConnections.Open(fixture.Source.Database.DatabasePath, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE notifications.notification_delivery_transitions SET document_json=json_set(document_json,'$.unexpected',true);";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var before = fixture.Source.Snapshot();
        var error = Assert.Throws<JsonException>(() => DeliveryReferenceIndex.IsSecretReferenced(fixture.Source.Instance,
            fixture.Source.Database, new(), old.SecretId, old.Digest, old.SizeBytes));
        Assert.Equal("$.unexpected", error.Path);
        var after = fixture.Source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
        Assert.Equal(original, File.ReadAllBytes(payload));
    }
}
