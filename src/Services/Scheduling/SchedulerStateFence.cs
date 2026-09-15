using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>
/// 调度 occurrence 的进程内恢复围栏：维护 pending/replay 状态、恢复冻结计划并生成 durable snapshot。
/// 文件读写仍由 ISchedulerStateStore 实现，Scheduler 只负责业务编排与触发状态机。
/// </summary>
internal sealed class SchedulerStateFence
{
    private readonly object _sync;

    private readonly object _saveSync = new();

    private readonly ISchedulerStateStore _stateStore;

    private readonly ExecutionPlanBuilder? _plans;

    private readonly SchedulerRetryQueue _retryQueue;

    internal SchedulerStateFence(
        object sync,
        ISchedulerStateStore stateStore,
        ExecutionPlanBuilder? plans,
        SchedulerRetryQueue retryQueue)
    {
        _sync = sync;
        _stateStore = stateStore;
        _plans = plans;
        _retryQueue = retryQueue;
    }

    internal Dictionary<string, ScheduledOccurrence> Occurrences { get; } = new(StringComparer.Ordinal);

    internal DateTime? LastSchedulerCheck { get; set; }

    internal bool StartupRunsIssued { get; private set; }

    internal void MarkStartupRunsIssuedLocked()
    {
        StartupRunsIssued = true;
    }

    internal bool StateDirty { get; private set; }

    private long StateRevision { get; set; }

    /// <summary>调用方已持有 Scheduler 的同步锁时标记状态发生 durable 变化。</summary>
    internal void MarkDirtyLocked()
    {
        StateDirty = true;
        StateRevision++;
    }

    internal void Restore()
    {
        SchedulerPersistedState state;
        try
        {
            state = _stateStore.Load();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[调度] 读取 scheduler-state.json 失败，按空状态启动：{ex.Message}");
            return;
        }
        LastSchedulerCheck = state.LastSchedulerCheck;
        lock (_sync)
        {
            foreach (PersistedScheduledOccurrence item in state.Occurrences)
            {
                if (string.IsNullOrWhiteSpace(item.QueueId) || string.IsNullOrWhiteSpace(item.OccurrenceKey))
                {
                    continue;
                }
                if (!RequiresRecovery(item.Status))
                {
                    // terminal occurrence 不再作为 durable 去重表永久保存；LastSchedulerCheck
                    // 与 recovery state 已经组成 replay fence。
                    continue;
                }
                QueueExecutionPlan? plan = null;
                if (item.Plan is not null && _plans is not null)
                {
                    try
                    {
                        plan = _plans.RestoreFrozenQueue(item.Plan);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[调度] 恢复队列「{item.QueueName}」冻结计划失败，将在下次重校验：{ex.Message}");
                    }
                }
                var occurrence = new ScheduledOccurrence
                {
                    QueueId = item.QueueId,
                    QueueName = item.QueueName,
                    OccurrenceKey = item.OccurrenceKey,
                    OriginalTriggerTime = item.OriginalTriggerTime,
                    IsStartup = item.IsStartup,
                    Status = item.Status == "Running" ? "Waiting" : item.Status,
                    RetryCount = item.RetryCount,
                    LastReason = item.LastReason,
                    NextAttemptAt = item.Status == "Running" ? DateTime.Now : item.NextAttemptAt,
                    Plan = plan,
                };
                Occurrences[occurrence.Key] = occurrence;
                if (occurrence.Status is "Triggered" or "Waiting")
                {
                    _retryQueue.RestorePending(occurrence);
                }
                if (occurrence.IsStartup && (occurrence.Status is "Triggered" or "Waiting" or "Running"))
                {
                    StartupRunsIssued = true;
                }
            }
        }
    }

    internal void Save(bool force = false)
    {
        // 同一 scheduler 可能同时收到 tick、准入和完成回调；序列化整个快照-写入过程，
        // 避免较旧 snapshot 在较新 snapshot 之后完成而覆盖 durable replay fence。
        lock (_saveSync)
        {
            SchedulerPersistedState snapshot;
            long revision;
            DateTime? replayFence;
            lock (_sync)
            {
                if (!force && !StateDirty)
                {
                    return;
                }
                revision = StateRevision;
                replayFence = LastSchedulerCheck;
                snapshot = new SchedulerPersistedState
                {
                    LastSchedulerCheck = LastSchedulerCheck,
                    Occurrences = Occurrences.Values
                        .Where(item => RequiresRecovery(item.Status))
                        .Select(item => new PersistedScheduledOccurrence
                        {
                            Key = item.Key,
                            QueueId = item.QueueId,
                            QueueName = item.QueueName,
                            OccurrenceKey = item.OccurrenceKey,
                            OriginalTriggerTime = item.OriginalTriggerTime,
                            IsStartup = item.IsStartup,
                            Status = item.Status,
                            RetryCount = item.RetryCount,
                            LastReason = item.LastReason,
                            NextAttemptAt = item.NextAttemptAt,
                            Plan = item.Plan is null ? null : ExecutionPlanBuilder.FreezeQueue(item.Plan),
                        })
                        .ToList(),
                };
            }

            try
            {
                _stateStore.Save(snapshot);
                lock (_sync)
                {
                    // 只有本次 snapshot 仍是最新版本时，才能清除 dirty 并释放已经越过
                    // durable replay fence 的 terminal occurrence。
                    if (StateRevision == revision)
                    {
                        StateDirty = false;
                        if (replayFence is DateTime fence)
                        {
                            foreach (string key in Occurrences.Values
                                         .Where(item => !RequiresRecovery(item.Status) && item.OriginalTriggerTime <= fence)
                                         .Select(item => item.Key)
                                         .ToArray())
                            {
                                Occurrences.Remove(key);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[调度] 保存 scheduler-state.json 失败：{ex.Message}");
            }
        }
    }

    private static bool RequiresRecovery(string status)
    {
        return status is "Triggered" or "Waiting" or "Running";
    }
}
