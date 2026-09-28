using NexusPipeline.Modules.Execution;

namespace NexusPipeline.Modules.Execution.Realtime;

internal static class RealtimeEventProjection
{
    internal static object RunStatus(RunningExecutionStatusSnapshot snapshot, bool active)
    {
        return new
        {
            runId = snapshot.Id,
            active,
            kind = snapshot.Kind,
            targetId = snapshot.TargetId,
            targetName = snapshot.TargetName,
            mode = snapshot.Mode,
            status = snapshot.Status,
            startedAt = snapshot.StartedAt,
            finishedAt = snapshot.FinishedAt,
            totalTasks = snapshot.TotalTasks,
            doneTasks = snapshot.DoneTasks,
            currentScriptName = snapshot.CurrentScriptName,
            currentScriptId = snapshot.CurrentScriptId,
            currentStatus = snapshot.CurrentStatus,
            currentAttempt = snapshot.CurrentAttempt,
            currentMaxAttempts = snapshot.CurrentMaxAttempts,
            persistenceWarning = snapshot.PersistenceWarning,
            logTruncated = snapshot.LogTruncated,
            logSegmentId = snapshot.LogSegmentId,
            logSegmentSequence = snapshot.LogSegmentSequence,
            logSegment = snapshot.LogSegment,
            cancelRequested = snapshot.CancelRequested,
            cancellationPhase = snapshot.CancellationPhase,
        };
    }

    internal static object RunLog(string runId, IReadOnlyList<ExecutionLogEntry> entries)
    {
        return new
        {
            runId,
            entries = entries.Select(entry => new
            {
                sequence = entry.Sequence,
                logSegmentId = entry.LogSegmentId,
                timestamp = entry.Timestamp,
                level = entry.Level.ToString().ToLowerInvariant(),
                message = entry.Message,
                formattedText = entry.FormattedText,
            }).ToArray(),
        };
    }

    internal static object SystemAction(
        string state,
        string action,
        string queueName,
        DateTime? deadline)
    {
        return new
        {
            state,
            action,
            queueName,
            deadline,
        };
    }

    internal static object HostStatus(IReadOnlyList<RunningExecutionStatusSnapshot> active)
    {
        return new
        {
            activeCount = active.Count,
        };
    }
}
