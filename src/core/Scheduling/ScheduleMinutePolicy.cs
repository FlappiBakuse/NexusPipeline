using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Automation;

namespace NexusPipeline.Core.Scheduling;

public sealed record ScheduleMinuteWatermark(long Revision, long? HighestUtcMinute);
public sealed record ScheduledOccurrenceProposal(Guid OccurrenceId, Guid RunId, Guid StartOperationId, Guid ScheduleId,
    long ScheduleRevision, Guid QueueId, long QueueRevision, long CatalogRevision, DateOnly LocalDate, int LocalMinute,
    string TimeZoneId, long TriggerUtcMinute);
public sealed record ScheduleMinuteScan(long ExpectedWatermarkRevision, long CatalogRevision, long HighestUtcMinute,
    ImmutableArray<ScheduledOccurrenceProposal> Occurrences);
public sealed record ScheduledMaintenanceBlocker(Guid ScheduleId, Guid QueueId, DateTimeOffset TriggerUtc);
public sealed record ScheduleDiagnostic(Guid ScheduleId, string ReasonCode);
public sealed record AccountNextRun(Guid UserId, Guid QueueId, DateTimeOffset NextRunAt);

public static class ScheduleMinutePolicy
{
    public static IReadOnlyList<AccountNextRun> NextForUsers(AutomationCatalog catalog, DateTimeOffset now)
    {
        AutomationCatalogPolicy.Validate(catalog); RequireNow(now);
        var users = catalog.Users.ToDictionary(item => item.Id);
        var participants = catalog.Bindings.Select(binding => AutomationBindingPolicy.Resolve(users[binding.UserId], binding))
            .Where(binding => binding.IsEnabled && binding.RunDays != 0).GroupBy(binding => binding.ScriptId)
            .ToDictionary(group => group.Key, group => group.Select(binding => binding.UserId).Distinct().ToArray());
        var queueUsers = catalog.Queues.Where(queue => queue.ScheduleEnabled).ToDictionary(queue => queue.Id,
            queue => queue.ScriptIds.Where(participants.ContainsKey).SelectMany(script => participants[script]).Distinct().ToArray());
        var result = new Dictionary<Guid, AccountNextRun>();
        foreach (var schedule in catalog.Schedules.Where(item => item.IsEnabled && queueUsers.ContainsKey(item.QueueId)))
        {
            var zone = TryTimeZone(schedule.TimeZoneId);
            if (zone is null) continue;
            var today = TimeZoneInfo.ConvertTime(now, zone).Date;
            DateTimeOffset? next = null;
            for (var day = 0; day <= 7; day++)
            {
                var local = DateTime.SpecifyKind(today.AddDays(day).AddMinutes(schedule.LocalMinute), DateTimeKind.Unspecified);
                if (!schedule.Weekdays.Contains((int)local.DayOfWeek) || zone.IsInvalidTime(local)) continue;
                var offsets = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local) : new[] { zone.GetUtcOffset(local) };
                foreach (var offset in offsets)
                {
                    var candidate = new DateTimeOffset(local, offset).ToUniversalTime();
                    if (candidate > now && (next is null || candidate < next)) next = candidate;
                }
                if (next is not null) break;
            }
            if (next is null) continue;
            foreach (var user in queueUsers[schedule.QueueId])
                if (!result.TryGetValue(user, out var prior) || next < prior.NextRunAt || next == prior.NextRunAt &&
                    StringComparer.Ordinal.Compare(schedule.QueueId.ToString("D"), prior.QueueId.ToString("D")) < 0)
                    result[user] = new(user, schedule.QueueId, next.Value);
        }
        return result.Values.OrderBy(item => item.UserId).ToArray();
    }

    public static ScheduleMinuteScan Scan(Guid instanceId, AutomationCatalog catalog, ScheduleMinuteWatermark watermark, DateTimeOffset now,
        Action<ScheduleDiagnostic>? report = null)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(watermark);
        AutomationCatalogPolicy.Validate(catalog);
        RequireNow(now);
        if (instanceId == Guid.Empty || watermark.Revision < 0 || watermark.HighestUtcMinute is < 0)
            throw new ArgumentException("schedule_scan_identity_invalid");
        var minute = UtcMinute(now);
        var highest = Math.Max(minute, watermark.HighestUtcMinute ?? minute);
        if (watermark.HighestUtcMinute >= minute)
            return new(watermark.Revision, catalog.Revision, highest, []);
        var queues = catalog.Queues.ToDictionary(queue => queue.Id);
        var candidates = ImmutableArray.CreateBuilder<ScheduledOccurrenceProposal>();
        var dueQueues = new HashSet<Guid>();
        foreach (var schedule in catalog.Schedules.OrderBy(item => item.Id.ToString("D"), StringComparer.Ordinal))
        {
            if (!schedule.IsEnabled || !queues[schedule.QueueId].ScheduleEnabled || queues[schedule.QueueId].ScriptIds.IsEmpty) continue;
            var zone = TryTimeZone(schedule.TimeZoneId);
            if (zone is null) { report?.Invoke(new(schedule.Id, "schedule_time_zone_unavailable")); continue; }
            var local = TimeZoneInfo.ConvertTime(FromUtcMinute(minute), zone);
            if (local.Hour * 60 + local.Minute != schedule.LocalMinute || !schedule.Weekdays.Contains((int)local.DayOfWeek)) continue;
            var date = DateOnly.FromDateTime(local.DateTime);
            if (!dueQueues.Add(schedule.QueueId)) continue;
            var key = schedule.QueueId.ToString("D") + "/" + schedule.TimeZoneId + "/" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "/" +
                schedule.LocalMinute.ToString(CultureInfo.InvariantCulture);
            candidates.Add(new(Id(instanceId, key, "occurrence"), Id(instanceId, key, "run"), Id(instanceId, key, "start"),
                schedule.Id, schedule.Revision, schedule.QueueId, queues[schedule.QueueId].Revision, catalog.Revision, date,
                schedule.LocalMinute, schedule.TimeZoneId, minute));
        }
        // A scan considers only this minute. An old checkpoint never creates work for missed minutes.
        return new(watermark.Revision, catalog.Revision, highest, candidates.ToImmutable());
    }

    public static ScheduledMaintenanceBlocker? Upcoming(AutomationCatalog catalog, DateTimeOffset now, TimeSpan horizon)
    {
        ArgumentNullException.ThrowIfNull(catalog); AutomationCatalogPolicy.Validate(catalog); RequireNow(now);
        if (horizon < TimeSpan.Zero || horizon > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(horizon));
        var queues = catalog.Queues.ToDictionary(queue => queue.Id);
        var schedules = catalog.Schedules.Where(schedule => schedule.IsEnabled && queues[schedule.QueueId].ScheduleEnabled && !queues[schedule.QueueId].ScriptIds.IsEmpty)
            .Select(schedule => (Schedule: schedule, Zone: TryTimeZone(schedule.TimeZoneId))).Where(item => item.Zone is not null)
            .OrderBy(item => item.Schedule.Id.ToString("D"), StringComparer.Ordinal).ToArray();
        var end = UtcMinute(now + horizon);
        for (var minute = UtcMinute(now); minute <= end; minute++)
        {
            foreach (var (schedule, zone) in schedules)
            {
                var local = TimeZoneInfo.ConvertTime(FromUtcMinute(minute), zone!);
                if (local.Hour * 60 + local.Minute == schedule.LocalMinute && schedule.Weekdays.Contains((int)local.DayOfWeek))
                    return new(schedule.Id, schedule.QueueId, FromUtcMinute(minute));
            }
        }
        return null;
    }

    public static IReadOnlyList<ScheduledProviderOccurrence> ProviderOccurrences(Guid instanceId,
        ObservedWorkPlans observed, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(observed); RequireNow(now); WorkPlanPolicy.Validate(observed.Plans);
        if (instanceId == Guid.Empty) throw new ArgumentException("scheduled_provider_instance_invalid");
        return observed.Entry.Use<IReadOnlyList<ScheduledProviderOccurrence>>(() =>
        {
            var result = new List<ScheduledProviderOccurrence>(); var minute = UtcMinute(now);
            foreach (var task in observed.Plans.Tasks)
            {
                if (!task.IsEnabled) continue;
                var local = TimeZoneInfo.ConvertTime(FromUtcMinute(minute), TimeZoneInfo.FindSystemTimeZoneById(task.TimeZoneId));
                if (local.Hour * 60 + local.Minute != task.LocalMinute || !task.Weekdays.Contains((int)local.DayOfWeek)) continue;
                result.Add(DeriveProviderOccurrence(instanceId, observed.Entry.ExtensionId, observed.Entry.ProviderId,
                    observed.Plans.Revision, task, now));
            }
            return result.AsReadOnly();
        });
    }

    public static ScheduledProviderOccurrence DeriveProviderOccurrence(Guid instanceId, string extensionId, string providerId,
        long plansRevision, WorkTaskPlan task, DateTimeOffset observedAtUtc)
    {
        RequireNow(observedAtUtc); Extensions.ExtensionIdentifiers.Require(extensionId); Extensions.ExtensionIdentifiers.Require(providerId);
        WorkPlanPolicy.Validate(new(plansRevision, [task]));
        if (instanceId == Guid.Empty || !task.IsEnabled) throw new ArgumentException("scheduled_provider_occurrence_invalid");
        var minute = UtcMinute(observedAtUtc);
        var local = TimeZoneInfo.ConvertTime(FromUtcMinute(minute), TimeZoneInfo.FindSystemTimeZoneById(task.TimeZoneId));
        if (local.Hour * 60 + local.Minute != task.LocalMinute || !task.Weekdays.Contains((int)local.DayOfWeek))
            throw new ArgumentException("scheduled_provider_not_due");
        var date = DateOnly.FromDateTime(local.DateTime);
        var key = extensionId + "/" + providerId + "/" + task.TaskId.ToString("D") + "/" +
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "/" + task.LocalMinute.ToString(CultureInfo.InvariantCulture);
        return new(new FrozenScheduledInvocation(instanceId, Id(instanceId, key, "provider-occurrence"),
            Id(instanceId, key, "provider-run"), Id(instanceId, key, "provider-attempt-1"), task.TaskId,
            Id(instanceId, key, "provider-notification"), observedAtUtc.AddSeconds(task.TimeoutSeconds), task.NotificationTaskIds),
            task, plansRevision, minute, date);
    }

    private static Guid Id(Guid instance, string key, string domain) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("nexus.schedule/1/" + domain + "/" + instance.ToString("D") + "/" + key)).AsSpan(0, 16), bigEndian: true);
    private static TimeZoneInfo? TryTimeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }
    private static long UtcMinute(DateTimeOffset value) => value.ToUnixTimeSeconds() / 60;
    private static DateTimeOffset FromUtcMinute(long value) => DateTimeOffset.FromUnixTimeSeconds(checked(value * 60));
    private static void RequireNow(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero || value < DateTimeOffset.UnixEpoch)
            throw new ArgumentException("schedule_clock_invalid");
    }
}
