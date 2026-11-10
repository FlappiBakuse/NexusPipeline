using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal sealed class ScheduledProviderCodec(RuntimeInstance instance, ContractReader contracts)
{
    internal BlobReference Persist<T>(T document) => new ImmutableBlobStore(instance, contracts).PersistPlan(Encode(document));
    internal static byte[] Encode<T>(T document) => BoundedJson.Serialize(document, StoredPlanFormat.Options);

    internal T ReadDocument<T>(BlobReference reference)
    {
        if (!StoredPlanFormat.IsReference(reference)) throw Invalid();
        using var held = new ImmutableBlobStore(instance, contracts).OpenVerified(reference);
        var bytes = new byte[checked((int)held.Length)]; held.ReadExactly(bytes);
        using var parsed = StrictJson.Parse(bytes);
        var result = JsonSerializer.Deserialize<T>(bytes, StoredPlanFormat.Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(parsed.RootElement, JsonSerializer.SerializeToElement(result, StoredPlanFormat.Options))) throw Invalid();
        return result;
    }

    internal ScheduledProviderSnapshot Read(BlobReference reference)
    {
        var document = ReadDocument<ScheduledProviderDocument>(reference);
        Require(document);
        return new(document, reference);
    }

    internal void Require(ScheduledProviderDocument document)
    {
        if (document.Format != "nexus.schedule.provider-occurrence" || document.Version != 1 ||
            document.InstanceId != instance.InstanceId || document.ActivationId == Guid.Empty || document.OwnerGeneration == Guid.Empty ||
            document.Invocation is null || document.Task is null || document.Creator is null || document.Creator.Process is null ||
            document.OperationId != document.Invocation.OccurrenceId || document.Invocation.InstanceId != instance.InstanceId ||
            document.TriggerUtcMinute < 0 || document.MinuteOperationId != ScheduleMinuteCodec.OperationId(instance.InstanceId, document.TriggerUtcMinute) ||
            document.PlansRevision < 0 || document.Invocation.SubjectId != document.Task.TaskId ||
            !document.Invocation.NotificationTaskIds.SequenceEqual(document.Task.NotificationTaskIds) ||
            document.InitialConsumedMilliseconds < 0 || document.InitialConsumedMilliseconds > document.Task.TimeoutSeconds * 1000L)
            throw Invalid();
        WorkPlanPolicy.Validate(new(document.PlansRevision, ImmutableArray.Create(document.Task)));
        var plans = ReadDocument<ScheduledWorkPlans>(document.Plans);
        WorkPlanPolicy.Validate(plans);
        if (plans.Revision != document.PlansRevision || !plans.Tasks.Any(task =>
            Encode(task).AsSpan().SequenceEqual(Encode(document.Task)))) throw Invalid();
        var expected = ScheduleMinutePolicy.DeriveProviderOccurrence(instance.InstanceId, document.ExtensionId, document.ProviderId,
            document.PlansRevision, document.Task, document.ObservedAtUtc);
        if (!expected.Invocation.Matches(document.Invocation) || expected.TriggerUtcMinute != document.TriggerUtcMinute ||
            expected.LocalDate != document.LocalDate) throw Invalid();
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(checked(document.TriggerUtcMinute * 60)),
            TimeZoneInfo.FindSystemTimeZoneById(document.Task.TimeZoneId));
        if (!document.Task.IsEnabled || DateOnly.FromDateTime(local.DateTime) != document.LocalDate ||
            local.Hour * 60 + local.Minute != document.Task.LocalMinute || !document.Task.Weekdays.Contains((int)local.DayOfWeek)) throw Invalid();
        JournalWriterPolicy.Require(document.Creator);
        WorkCalendar.Require(document.ObservedAtUtc, document.HostTimeZoneId, document.HostLocalDate);
        if (document.ManualTask is not null) ManualWorkPolicy.RequirePlan(document.ManualTask, document.Task);
        else if (document.Task.OwnerTaskId is not null) throw Invalid();
    }

    internal static IOException Invalid() => new("scheduled_provider_source_unbound");
}
