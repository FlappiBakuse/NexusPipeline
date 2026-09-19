using NexusPipeline.Modules.Execution;
using NexusPipeline.Platform.Testing;

namespace NexusPipeline.Modules.Scheduling;

/// <summary>
/// 管理待触发 occurrence 的内存运行状态。持久化由 SchedulerStateFence 协调。
/// 调用方和本组件共享 Scheduler 的同步锁，所有集合访问都在该锁下完成。
/// </summary>
internal sealed class SchedulerRetryQueue
{
    private readonly object _sync;
    private readonly Dictionary<string, ScheduledOccurrence> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _attempting = new(StringComparer.Ordinal);
    private readonly HashSet<string> _runningQueueIds = new(StringComparer.Ordinal);

    internal SchedulerRetryQueue(object sync)
    {
        _sync = sync;
    }

    internal int AttemptingCount
    {
        get
        {
            lock (_sync)
            {
                return _attempting.Count;
            }
        }
    }

    internal bool IsAnyQueueRunning
    {
        get
        {
            lock (_sync)
            {
                return _runningQueueIds.Count > 0;
            }
        }
    }

    internal IReadOnlyList<ScheduledOccurrence> SnapshotPending(
        Func<ScheduledOccurrence, bool>? predicate = null)
    {
        lock (_sync)
        {
            return _pending.Values
                .Where(item => predicate is null || predicate(item))
                .ToArray();
        }
    }

    internal bool TryGetPending(string key, out ScheduledOccurrence? occurrence)
    {
        lock (_sync)
        {
            return _pending.TryGetValue(key, out occurrence);
        }
    }

    internal bool ContainsPending(string key)
    {
        lock (_sync)
        {
            return _pending.ContainsKey(key);
        }
    }

    internal bool TryAddPending(ScheduledOccurrence occurrence)
    {
        lock (_sync)
        {
            return _pending.TryAdd(occurrence.Key, occurrence);
        }
    }

    internal void RestorePending(ScheduledOccurrence occurrence)
    {
        lock (_sync)
        {
            _pending[occurrence.Key] = occurrence;
        }
    }

    internal bool TryClaimAttempt(ScheduledOccurrence occurrence)
    {
        lock (_sync)
        {
            if (!_pending.TryGetValue(occurrence.Key, out ScheduledOccurrence? current)
                || !ReferenceEquals(current, occurrence)
                || occurrence.NextAttemptAt > DateTime.Now)
            {
                return false;
            }
            return _attempting.Add(occurrence.Key);
        }
    }

    internal void ReleaseAttempt(ScheduledOccurrence occurrence)
    {
        lock (_sync)
        {
            _attempting.Remove(occurrence.Key);
        }
    }

    internal bool IsQueueRunning(string queueId)
    {
        lock (_sync)
        {
            return _runningQueueIds.Contains(queueId);
        }
    }

    internal void MarkQueueRunning(string queueId)
    {
        lock (_sync)
        {
            _runningQueueIds.Add(queueId);
        }
    }

    internal void ReleaseQueue(string queueId)
    {
        lock (_sync)
        {
            _runningQueueIds.Remove(queueId);
        }
    }

    internal bool SetPlanIfCurrent(ScheduledOccurrence occurrence, QueueExecutionPlan plan)
    {
        lock (_sync)
        {
            if (!_pending.TryGetValue(occurrence.Key, out ScheduledOccurrence? current)
                || !ReferenceEquals(current, occurrence))
            {
                return false;
            }
            occurrence.Plan = plan;
            occurrence.QueueName = plan.Queue.Name;
            return true;
        }
    }

    internal bool ScheduleRetry(ScheduledOccurrence occurrence, string reason)
    {
        lock (_sync)
        {
            if (!_pending.TryGetValue(occurrence.Key, out ScheduledOccurrence? current)
                || !ReferenceEquals(current, occurrence))
            {
                return false;
            }
            occurrence.Status = "Waiting";
            occurrence.RetryCount++;
            occurrence.LastReason = reason;
            occurrence.NextAttemptAt = DateTime.Now.AddSeconds(TestHooks.ScaledSeconds(5));
            return true;
        }
    }

    internal bool MarkRunning(ScheduledOccurrence occurrence)
    {
        lock (_sync)
        {
            _pending.Remove(occurrence.Key);
            occurrence.Status = "Running";
            return true;
        }
    }

    internal bool RemovePending(string key)
    {
        lock (_sync)
        {
            return _pending.Remove(key);
        }
    }

    internal void MakeAllPendingDueForTest()
    {
        lock (_sync)
        {
            foreach (ScheduledOccurrence occurrence in _pending.Values)
            {
                occurrence.NextAttemptAt = DateTime.MinValue;
            }
        }
    }

    internal SchedulerTestSnapshot SnapshotForTest()
    {
        lock (_sync)
        {
            return new SchedulerTestSnapshot(
                _pending.Count,
                _pending.Values.Select(item => item.Status).FirstOrDefault(),
                _attempting.Count);
        }
    }
}

internal sealed record SchedulerTestSnapshot(
    int PendingCount,
    string? PendingStatus,
    int PendingAttemptCount);
