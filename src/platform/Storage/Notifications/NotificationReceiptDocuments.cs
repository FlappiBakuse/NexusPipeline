using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed record ReceiptTaskSecrets(
    [property: JsonRequired] Guid TaskId,
    [property: JsonRequired] ImmutableArray<NotificationDeliverySecret> Secrets);
internal sealed record NotificationReceiptDocument(
    [property: JsonRequired] string Format, [property: JsonRequired] int Version,
    [property: JsonRequired] Guid InstanceId, [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] string SubjectId,
    [property: JsonRequired] AttemptReceipt Receipt,
    [property: JsonRequired] string? DeclineReasonCode,
    [property: JsonRequired] BlobReference TotalPlan,
    [property: JsonRequired] BlobReference DefinitionBundle,
    [property: JsonRequired] BlobReference Catalog,
    [property: JsonRequired] long CatalogRevision,
    [property: JsonRequired] int RouteCount,
    [property: JsonRequired] ImmutableArray<ExecutionStepFact> OrderedFacts,
    [property: JsonRequired] ImmutableArray<ReceiptTaskSecrets> Tasks,
    [property: JsonRequired] RecordAsset? Screenshot,
    [property: JsonRequired] NexusPipeline.Core.Settings.HostLocale Locale);
internal sealed record NotificationReceiptSnapshot(NotificationReceiptDocument Document, BlobReference Reference);
internal sealed record ReceiptRouteDocument(
    [property: JsonRequired] string Format, [property: JsonRequired] int Version,
    [property: JsonRequired] Guid SourceOperationId,
    [property: JsonRequired] int Ordinal,
    [property: JsonRequired] string Kind,
    [property: JsonRequired] Guid SourceId,
    [property: JsonRequired] ImmutableArray<ReceiptRouteTarget> Targets);
internal sealed record ReceiptRouteTarget(
    [property: JsonRequired] Guid TaskId,
    [property: JsonRequired] string Disposition,
    [property: JsonRequired] Guid? DeliveryOperationId,
    [property: JsonRequired] BlobReference? Delivery);

internal sealed record NotificationBindingLabel(Guid BindingId, string ScriptName, string UserName,
    string SelectionDisposition, string SelectionReason, ImmutableArray<string> StepIds);
internal sealed record ReceiptReadContext(NotificationReceiptSnapshot Snapshot,
    NotificationCatalogSnapshot Catalog, ImmutableArray<NexusPipeline.Core.Notifications.NotificationRoute> Routes,
    ImmutableArray<NotificationBindingLabel> Bindings);
