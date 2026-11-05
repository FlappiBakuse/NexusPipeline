using System.Globalization;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Extensions;

internal static class ScheduledInvocationWire
{
    internal static ScheduledInvocation ToWire(FrozenScheduledInvocation invocation) => new()
    {
        Format = "nexus.schedule.invocation", Version = 1, InstanceId = invocation.InstanceId.ToString("D"),
        OccurrenceId = invocation.OccurrenceId.ToString("D"), RunId = invocation.RunId.ToString("D"), AttemptId = invocation.AttemptId.ToString("D"),
        SubjectId = invocation.SubjectId.ToString("D"), NotificationOperationId = invocation.NotificationOperationId.ToString("D"),
        DeadlineUtc = invocation.DeadlineUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
        NotificationTaskIds = Array.AsReadOnly(invocation.NotificationTaskIds.Select(id => id.ToString("D")).ToArray())
    };

    internal static FrozenScheduledInvocation FromWire(ScheduledInvocation invocation) => new(Guid.Parse(invocation.InstanceId),
        Guid.Parse(invocation.OccurrenceId), Guid.Parse(invocation.RunId), Guid.Parse(invocation.AttemptId), Guid.Parse(invocation.SubjectId),
        Guid.Parse(invocation.NotificationOperationId), DateTimeOffset.Parse(invocation.DeadlineUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        invocation.NotificationTaskIds.Select(Guid.Parse).ToArray());
}
