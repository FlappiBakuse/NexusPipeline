namespace NexusPipeline.Core.Scheduling;

public sealed class FrozenScheduledInvocation
{
    public Guid InstanceId { get; }
    public Guid OccurrenceId { get; }
    public Guid RunId { get; }
    public Guid AttemptId { get; }
    public Guid SubjectId { get; }
    public Guid NotificationOperationId { get; }
    public DateTimeOffset DeadlineUtc { get; }
    public IReadOnlyList<Guid> NotificationTaskIds { get; }

    public FrozenScheduledInvocation(Guid instanceId, Guid occurrenceId, Guid runId, Guid attemptId, Guid subjectId,
        Guid notificationOperationId, DateTimeOffset deadlineUtc, IReadOnlyList<Guid> notificationTaskIds)
    {
        ArgumentNullException.ThrowIfNull(notificationTaskIds);
        var ids = Array.AsReadOnly(notificationTaskIds.ToArray());
        if (new[] { instanceId, occurrenceId, runId, attemptId, subjectId, notificationOperationId }.Contains(Guid.Empty) ||
            deadlineUtc.Offset != TimeSpan.Zero || deadlineUtc <= DateTimeOffset.UnixEpoch || ids.Count > 99 || ids.Contains(Guid.Empty) || ids.Distinct().Count() != ids.Count)
            throw new ArgumentException("scheduled_invocation_identity_invalid");
        InstanceId = instanceId; OccurrenceId = occurrenceId; RunId = runId; AttemptId = attemptId; SubjectId = subjectId;
        NotificationOperationId = notificationOperationId; DeadlineUtc = deadlineUtc; NotificationTaskIds = ids;
    }

    public bool Matches(FrozenScheduledInvocation other) => InstanceId == other.InstanceId && OccurrenceId == other.OccurrenceId &&
        RunId == other.RunId && AttemptId == other.AttemptId && SubjectId == other.SubjectId && NotificationOperationId == other.NotificationOperationId &&
        DeadlineUtc == other.DeadlineUtc && NotificationTaskIds.SequenceEqual(other.NotificationTaskIds);
}
