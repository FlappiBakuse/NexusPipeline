using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage.Secrets;

namespace NexusPipeline.Platform.Storage.Notifications;

internal enum NotificationDeliveryState { Queued, Sending, Succeeded, Failed, Unknown, Skipped }
internal sealed record DeliveryReceiptSource(
    [property: JsonRequired] AttemptReceipt Receipt,
    [property: JsonRequired] BlobReference TotalPlan,
    [property: JsonRequired] BlobReference DefinitionBundle,
    [property: JsonRequired] string RouteKind,
    [property: JsonRequired] Guid RouteId);
[method: JsonConstructor]
internal sealed record NotificationDeliverySource(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] DeliveryReceiptSource? Execution,
    [property: JsonRequired] BlobReference? ExplicitSource)
{
    internal NotificationDeliverySource(AttemptReceipt receipt, BlobReference total, BlobReference bundle, string route, Guid routeId)
        : this("executionReceipt", new(receipt, total, bundle, route, routeId), null) { }
    [JsonIgnore] internal AttemptReceipt Receipt => RequireExecution().Receipt;
    [JsonIgnore] internal BlobReference TotalPlan => RequireExecution().TotalPlan;
    [JsonIgnore] internal BlobReference DefinitionBundle => RequireExecution().DefinitionBundle;
    [JsonIgnore] internal string RouteKind => RequireExecution().RouteKind;
    [JsonIgnore] internal Guid RouteId => RequireExecution().RouteId;
    private DeliveryReceiptSource RequireExecution() =>
        Kind == "executionReceipt" && Execution is not null && ExplicitSource is null ? Execution :
            throw new IOException("notification_delivery_source_invalid");
}
internal sealed record NotificationDeliverySecret(
    [property: JsonRequired] NotificationSecretField Field,
    [property: JsonRequired] SecretFieldReference Reference,
    [property: JsonRequired] SecretPayloadIdentity? Payload);
internal sealed record NotificationDeliveryDocument(
    [property: JsonRequired] string Format, [property: JsonRequired] int Version,
    [property: JsonRequired] Guid InstanceId, [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] Guid AcceptanceId, [property: JsonRequired] string SubjectId,
    [property: JsonRequired] NotificationDeliverySource Source,
    [property: JsonRequired] BlobReference Catalog,
    [property: JsonRequired] long CatalogRevision,
    [property: JsonRequired] NotificationCatalogEntry Task,
    [property: JsonRequired] string Title, [property: JsonRequired] string Body,
    [property: JsonRequired] ImmutableArray<NotificationDeliverySecret> Secrets,
    [property: JsonRequired] RecordAsset? Attachment,
    [property: JsonRequired] BlobReference? Registration = null,
    [property: JsonRequired] int? RouteOrdinal = null);
internal sealed record NotificationDeliverySnapshot(NotificationDeliveryDocument Document, BlobReference Reference);
internal sealed record NotificationDeliveryTransition(
    [property: JsonRequired] string Format, [property: JsonRequired] int Version,
    [property: JsonRequired] Guid OperationId, [property: JsonRequired] int Ordinal,
    [property: JsonRequired] NotificationDeliveryState State,
    [property: JsonRequired] Guid OwnerGeneration,
    [property: JsonRequired] NotificationDeliveryOwner? Sender,
    [property: JsonRequired] string ReasonCode,
    [property: JsonRequired] int? ProtocolStatusCode,
    [property: JsonRequired] NotificationDeliveryNetwork? Network = null);

internal static class NotificationDeliveryStates
{
    internal static bool ReleasesReferences(NotificationDeliveryState state) =>
        state is NotificationDeliveryState.Succeeded or NotificationDeliveryState.Failed or NotificationDeliveryState.Skipped;

    internal static void RequireSuccessor(NotificationDeliveryTransition? before, NotificationDeliveryTransition after)
    {
        if (after.Format != "nexus.notification.delivery-transition" || after.Version != 1 ||
            after.OperationId == Guid.Empty || after.OwnerGeneration == Guid.Empty ||
            !Enum.IsDefined(after.State) || after.ProtocolStatusCode is < 100 or > 599)
            throw new IOException("notification_delivery_transition_invalid");
        if (after.Network is { } network) DeliveryNetworkEvidence.RequireShape(network);
        if ((after.State is NotificationDeliveryState.Queued or NotificationDeliveryState.Skipped) && after.Network is not null)
            throw new IOException("notification_delivery_network_unbound");
        if (after.Sender is { } sender) _ = sender.ToProcessIdentity();
        if (after.State is NotificationDeliveryState.Queued or NotificationDeliveryState.Skipped
            ? after.Sender is not null : after.Sender is null)
            throw new IOException("notification_delivery_sender_unbound");
        var valid = before is null ? after.Ordinal == 0 && after.State == NotificationDeliveryState.Queued :
            after.OperationId == before.OperationId && after.Ordinal == before.Ordinal + 1 &&
            (before.State == NotificationDeliveryState.Queued && after.State is NotificationDeliveryState.Sending or NotificationDeliveryState.Skipped ||
             before.State == NotificationDeliveryState.Sending && after.State is NotificationDeliveryState.Succeeded or NotificationDeliveryState.Failed or NotificationDeliveryState.Unknown);
        if (!valid || after.ReasonCode != Reason(after.State) ||
            after.State is not (NotificationDeliveryState.Succeeded or NotificationDeliveryState.Failed) && after.ProtocolStatusCode is not null)
            throw new IOException("notification_delivery_transition_invalid");
        if (before is not null && before.State == NotificationDeliveryState.Sending &&
            (after.Sender != before.Sender || after.Network != before.Network || after.State != NotificationDeliveryState.Unknown && after.OwnerGeneration != before.OwnerGeneration))
            throw new IOException("notification_delivery_sender_unbound");
    }

    internal static string Reason(NotificationDeliveryState state) => state switch
    {
        NotificationDeliveryState.Queued => "notification_queued",
        NotificationDeliveryState.Sending => "notification_sending",
        NotificationDeliveryState.Succeeded => "notification_sent",
        NotificationDeliveryState.Failed => "notification_rejected",
        NotificationDeliveryState.Unknown => "notification_delivery_unknown",
        NotificationDeliveryState.Skipped => "notification_skipped",
        _ => throw new IOException("notification_delivery_transition_invalid")
    };
}
