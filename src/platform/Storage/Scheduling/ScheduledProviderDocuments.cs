using System.Text.Json.Serialization;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal sealed record ScheduledProviderDocument(
    [property: JsonRequired] string Format, [property: JsonRequired] int Version,
    [property: JsonRequired] Guid InstanceId, [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] Guid MinuteOperationId, [property: JsonRequired] long TriggerUtcMinute,
    [property: JsonRequired] DateTimeOffset ObservedAtUtc,
    [property: JsonRequired] string ExtensionId, [property: JsonRequired] string ProviderId,
    [property: JsonRequired] Guid ActivationId, [property: JsonRequired] Guid OwnerGeneration,
    [property: JsonRequired] BlobReference Plans, [property: JsonRequired] long PlansRevision,
    [property: JsonRequired] FrozenScheduledInvocation Invocation, [property: JsonRequired] WorkTaskPlan Task,
    [property: JsonRequired] DateOnly LocalDate, [property: JsonRequired] JournalWriter Creator,
    [property: JsonRequired] DateOnly HostLocalDate, [property: JsonRequired] string HostTimeZoneId,
    [property: JsonRequired] long InitialConsumedMilliseconds, [property: JsonRequired] ManualWorkTask? ManualTask);
internal sealed record ScheduledProviderSnapshot(ScheduledProviderDocument Document, BlobReference Reference);
internal sealed record ScheduledProviderBegin(
    [property: JsonRequired] string Format, [property: JsonRequired] int Version,
    [property: JsonRequired] BlobReference Source, [property: JsonRequired] DateTimeOffset ObservedAtUtc,
    [property: JsonRequired] long ConsumedMilliseconds);
internal sealed record ScheduledProviderResult(
    [property: JsonRequired] string Format, [property: JsonRequired] int Version,
    [property: JsonRequired] BlobReference Source, [property: JsonRequired] BlobReference? Begin,
    [property: JsonRequired] string Completion, [property: JsonRequired] ScheduledBusinessResult? Returned,
    [property: JsonRequired] long? FinalConsumedMilliseconds, [property: JsonRequired] DateTimeOffset ObservedAtUtc,
    [property: JsonRequired] bool WallClockRegressed, [property: JsonRequired] string ReasonCode);
internal sealed record ScheduledProviderClose(
    [property: JsonRequired] string Format, [property: JsonRequired] int Version,
    [property: JsonRequired] BlobReference Source, [property: JsonRequired] string ReasonCode);
