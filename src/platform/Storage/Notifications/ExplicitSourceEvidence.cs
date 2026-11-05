using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed class ExplicitSourceEvidence(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal void RequireDelivery(NotificationDeliverySnapshot delivery, SqliteConnection connection, SqliteTransaction transaction)
    {
        if (delivery.Document.Source.Kind == "manualInvocation")
        {
            var manualCodec = new NotificationManualCodec(instance, contracts);
            var manual = manualCodec.Read(delivery.Document.Source.ExplicitSource ?? throw NotificationManualCodec.Invalid(), connection, transaction);
            var target = manual.Document.Targets.SingleOrDefault(item => item.TaskId == delivery.Document.Task.TaskId) ?? throw NotificationManualCodec.Invalid();
            if (!NotificationCatalogCodec.Encode(delivery.Document).AsSpan().SequenceEqual(NotificationCatalogCodec.Encode(manualCodec.Delivery(manual, target))))
                throw NotificationManualCodec.Invalid();
            NotificationManualRows.RequireMembership(connection, transaction, delivery); return;
        }
        if (delivery.Document.Source.Kind == "scheduledInvocation")
        {
            var scheduledCodec = new NotificationScheduledCodec(instance, contracts);
            var scheduled = scheduledCodec.Read(delivery.Document.Source.ExplicitSource ?? throw NotificationScheduledCodec.Invalid(), connection, transaction);
            var target = scheduled.Document.Targets.SingleOrDefault(item => item.TaskId == delivery.Document.Task.TaskId) ?? throw NotificationScheduledCodec.Invalid();
            var expectedScheduled = scheduledCodec.Delivery(scheduled, target);
            if (!NotificationCatalogCodec.Encode(delivery.Document).AsSpan().SequenceEqual(NotificationCatalogCodec.Encode(expectedScheduled)))
                throw NotificationScheduledCodec.Invalid();
            NotificationScheduledRows.RequireMembership(connection, transaction, delivery);
            return;
        }
        if (delivery.Document.Source.Kind != "savedTaskTest" || delivery.Document.Source.ExplicitSource is null)
            throw TaskTestCodec.Invalid();
        var codec = new TaskTestCodec(instance, database, contracts);
        var source = codec.Read(delivery.Document.Source.ExplicitSource, connection, transaction);
        var expected = codec.Delivery(source);
        if (!NotificationCatalogCodec.Encode(delivery.Document).AsSpan().SequenceEqual(NotificationCatalogCodec.Encode(expected)))
            throw TaskTestCodec.Invalid();
        TaskTestRows.RequireMembership(connection, transaction, delivery);
    }
}
