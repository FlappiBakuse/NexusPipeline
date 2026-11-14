using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class DeliveryReferenceFixture : IDisposable
{
    internal ExecutionRuntimeFixture Runtime { get; } = new();
    internal NotificationCatalogService Catalog { get; }
    internal Guid TaskId { get; } = Guid.NewGuid();
    internal NotificationTaskInput Input { get; } = new("delivery", "", NotificationTaskKind.Webhook, true,
        NotificationChannelConfiguration.Default with { WebhookKind = NotificationWebhookKind.Generic });
    internal NotificationDeliverySnapshot Snapshot { get; private set; } = null!;
    internal PlanBuilderFixture Source => Runtime.Source;

    internal DeliveryReferenceFixture() => Catalog = new(Source.Instance, Source.Database,
        Source.Lease, Runtime.Base.Resources, Runtime.Base.Index, new());

    internal async Task Prepare()
    {
        var receipt = Assert.IsType<AttemptReceipt>(await Runtime.Consumer.RunNextAsync());
        using var secret = new SecretValue("https://notification-fixture.invalid/original");
        _ = await Catalog.SaveAsync(Guid.NewGuid(), "local:delivery-component", 0, TaskId, 0, Input,
            [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, secret)]);
        using var connection = DatabaseConnections.Open(Source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        var cursor = NotificationCatalogRows.ReadCurrent(connection, null)!;
        var catalog = new NotificationCatalogCodec(Source.Instance, new()).Read(cursor.Reference);
        var secrets = Enum.GetValues<NotificationSecretField>().Select(field =>
        {
            var reference = SecretFieldRows.ReadField(connection, null, SecretFieldKey.NotificationTask(Source.Instance.InstanceId, TaskId, field));
            SecretPayloadIdentity? payload = null;
            if (reference.SecretId is { } id)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT protection_scheme,owner_sid,digest,size_bytes FROM system.secret_payloads WHERE secret_id=$id;";
                command.Parameters.AddWithValue("$id", id.ToString("D"));
                using var row = command.ExecuteReader(); Assert.True(row.Read());
                payload = new(id, row.GetString(0), row.GetString(1), row.GetString(2), row.GetInt64(3));
            }
            return new NotificationDeliverySecret(field, reference, payload);
        }).ToImmutableArray();
        var state = Runtime.State;
        Snapshot = new NotificationDeliveryCodec(Source.Instance, new()).Persist(new("nexus.notification.delivery", 1,
            Source.Instance.InstanceId, Guid.NewGuid(), Guid.NewGuid(), ExecutionSequentialFixture.Subject,
            new(receipt, AttemptPlanCodec.Reference(state.Attempt.ExecutionPlan),
                new(state.Total.DefinitionBundle.RelativePath, state.Total.DefinitionBundle.SizeBytes, state.Total.DefinitionBundle.Sha256),
                "queue", Source.QueueId), cursor.Reference, cursor.Revision,
            Assert.Single(catalog.Document.Tasks), "actual receipt", "metadata component only", secrets, null));
    }

    internal void Accept(NotificationDeliverySnapshot? snapshot = null) => Source.Lease.PublishExecution(() =>
        Source.Database.Write((connection, transaction) =>
        {
            NotificationDeliveryRows.AcceptInside(connection, transaction, snapshot ?? Snapshot,
                DateTimeOffset.UtcNow, Source.Lease.StartupGeneration);
            return true;
        }));

    internal void Transition(NotificationDeliveryState state)
    {
        var actual = NotificationDeliveryOwner.Capture(JournalWriterStore.CurrentWriter);
        Source.Lease.PublishExecution(() => Source.Database.Write((connection, transaction) =>
        {
            var before = NotificationDeliveryRows.ReadState(connection, transaction, Snapshot.Document.OperationId);
            NotificationDeliveryRows.AppendInside(connection, transaction, Snapshot,
                new("nexus.notification.delivery-transition", 1, before.OperationId, before.Ordinal + 1, state,
                    Source.Lease.StartupGeneration, state == NotificationDeliveryState.Sending ? actual : before.Sender,
                    NotificationDeliveryStates.Reason(state), null));
            return true;
        }));
    }

    internal async Task Replace()
    {
        using var replacement = new SecretValue("https://notification-fixture.invalid/replacement");
        _ = await Catalog.SaveAsync(Guid.NewGuid(), "local:delivery-component", 1, TaskId, 1, Input,
            [new(NotificationSecretField.WebhookUrl, 1, SecretDisposition.Set, replacement)]);
    }

    internal long Operations()
    {
        using var connection = DatabaseConnections.Open(Source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT count(*) FROM main.operations;";
        return (long)command.ExecuteScalar()!;
    }

    public void Dispose() => Runtime.Dispose();
}
