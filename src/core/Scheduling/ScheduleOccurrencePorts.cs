using System.Collections.Immutable;
using NexusPipeline.Core.Execution;

namespace NexusPipeline.Core.Scheduling;

public enum ScheduleOccurrencePhase { WaitingForAdmission, Accepted, Completed, Blocked }
public sealed record ScheduledOccurrence(Guid InstanceId, ScheduledOccurrenceProposal Frozen, string SubjectId,
    ScheduleOccurrencePhase Phase, Guid? JournalId, Guid? AttemptId, string? ReasonCode);
public sealed record ScheduleCheckpoint(ScheduleMinuteWatermark Watermark, long CatalogRevision);

public interface IScheduleOccurrenceStore
{
    ScheduleCheckpoint ReadCheckpoint();
    ImmutableArray<ScheduledOccurrence> CommitMinute(ScheduleMinuteScan scan);
    ImmutableArray<ScheduledOccurrence> CommitCurrentMinute();
    ScheduledOccurrence BlockWaiting(ScheduledOccurrence expected, string reasonCode);
    ImmutableArray<ScheduledOccurrence> ReadWaiting(int limit);
}

public interface IScheduledExecutionAdmission
{
    // The adapter rechecks the actual durable occurrence and uses the ordinary execution admission owner.
    ValueTask<IExecutionAdmissionReceipt> StartAsync(ScheduledOccurrence occurrence, CancellationToken cancellation);
}

public sealed class ScheduleTick
{
    private readonly object _gate = new();
    private Task _active = Task.CompletedTask;
    private bool _stopping;

    public Task Run(Func<Task> tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        lock (_gate)
        {
            if (_stopping) throw new InvalidOperationException("schedule_stopping");
            if (!_active.IsCompleted) return _active;
            return _active = Task.Run(tick);
        }
    }

    public Task StopAsync()
    {
        lock (_gate) { _stopping = true; return _active; }
    }
}
