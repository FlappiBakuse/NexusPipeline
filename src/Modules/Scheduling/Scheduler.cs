using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Execution.Contracts;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.History.Contracts;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Queues.Contracts;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Scheduling.Contracts;
using NexusPipeline.Modules.Users.Bindings;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Modules.Users;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Modules.Scheduling;

internal sealed class Scheduler : IDisposable, IQueueScheduleProjection, ISchedulerIdleReader, IUserScheduleProjection
{
    private readonly object _sync = new();

    private readonly SchedulerRetryQueue _retryQueue;

    /// <summary>当前进程内仍待准入的 occurrence；terminal occurrence 仅在 replay fence 落盘前短暂保留用于去重。</summary>

    private CancellationTokenSource? _cts;

    private Task? _loop;

    private string? _lastCleanupDate;

    private readonly IQueueRepository _queues;

    private readonly IHistoryStore _history;

    private readonly ISettingsProvider _settings;

    private readonly IExecutionService _commands;

    private readonly IAdmissionCoordination? _coordination;

    private readonly ExecutionValidator _validator;

    private readonly ExecutionPlanBuilder? _plans;

    private readonly SchedulerStateFence _stateFence;

    private readonly IUserRunDaysMaintenance? _runDaysMaintenance;

    /// <summary>上次执行运行天数递减的本地日期（字符串比较，避免同一天重复递减）。</summary>
    private string? _lastRunDaysDecayDate;

    public Scheduler(
        IQueueRepository queues,
        IHistoryStore history,
        ISettingsProvider settings,
        IExecutionService commands,
        ExecutionValidator validator,
        ExecutionPlanBuilder? plans = null,
        ISchedulerStateStore? stateStore = null,
        IUserRunDaysMaintenance? runDaysMaintenance = null,
        IAdmissionCoordination? coordination = null)
    {
        _queues = queues;
        _history = history;
        _settings = settings;
        _commands = commands;
        _coordination = coordination;
        _validator = validator;
        _plans = plans;
        _retryQueue = new SchedulerRetryQueue(_sync);
        _stateFence = new SchedulerStateFence(
            _sync,
            stateStore ?? new MemorySchedulerStateStore(),
            _plans,
            _retryQueue);
        _runDaysMaintenance = runDaysMaintenance;
        // 启动当天不立即递减（避免每次重启就少一天）；只有运行期间跨天、或首次 tick 在启动后的次日触发才递减。
        _lastRunDaysDecayDate = DateTime.Now.ToString("yyyy-MM-dd");
        _stateFence.Restore();
    }

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }
        RevalidatePendingPlans();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        Logger.Info("调度器已启动。");
    }

    public bool IsRunning => _loop is not null && !_loop.IsCompleted;

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }
        try
        {
            _cts?.Dispose();
        }
        catch
        {
        }
        _cts = null;
        _loop = null;
        // 正常 Stop 是显式的 durable checkpoint：把最新 watermark 与 recovery state 一起写入，
        // 让本进程内已经完成的 occurrence 可以安全越过 replay fence。
        SaveState(force: true);
    }

    /// <summary>用户修改队列、脚本或用户后调用，显式重校验尚未准入的冻结计划。</summary>
    public void RevalidatePendingPlans()
    {
        ScheduledOccurrence[] pending = _retryQueue
            .SnapshotPending(item => item.Status is "Triggered" or "Waiting")
            .ToArray();

        foreach (ScheduledOccurrence item in pending)
        {
            DispatchQueue? current = _queues.Snapshot().FirstOrDefault(queue => queue.Id == item.QueueId);
            if (current is null)
            {
                InvalidatePending(item, "引用的调度队列已删除", saveHistory: false);
                continue;
            }
            if (!item.IsStartup && !SchedulerTriggerPlanner.MatchesOccurrence(current, item.OriginalTriggerTime))
            {
                InvalidatePending(item, "定时配置已变化，本次等待触发已取消", saveHistory: false, cancelled: true);
                continue;
            }

            if (_plans is null)
            {
                continue;
            }

            try
            {
                QueueExecutionPlan plan = _plans.BuildQueueForSchedule(current.Id);
                if (plan.Tasks.Any(task => task.Script is null))
                {
                    InvalidatePending(item, "等待计划引用的脚本实例已删除", saveHistory: false);
                    continue;
                }
                lock (_sync)
                {
                    if (_retryQueue.TryGetPending(item.Key, out ScheduledOccurrence? live)
                        && ReferenceEquals(live, item)
                        && (live.Status is "Triggered" or "Waiting"))
                    {
                        live.Plan = plan;
                        live.QueueName = plan.Queue.Name;
                        MarkStateDirtyLocked();
                    }
                }
            }
            catch (Exception ex)
            {
                InvalidatePending(item, ex.Message, saveHistory: false);
            }
        }
        SaveState();
    }

    /// <summary>计算下一次定时触发的调度队列（今天之后 7 天内的最近匹配时间点；仅定时模式，不含启动时运行）。</summary>
    public (string QueueName, DateTime TriggerTime)? NextTrigger()
    {
        DateTime now = DateTime.Now;
        var candidates = new List<(string Name, DateTime Time)>();
        List<DispatchQueue> queues = _queues.Snapshot().ToList();
        foreach (DispatchQueue queue in queues.Where(queue => queue.AutoRunMode == "scheduled" && queue.Tasks.Count > 0))
        {
            DateTime? time = SchedulerTriggerPlanner.NextTriggerFor(queue, now);
            if (time is not null)
            {
                candidates.Add((queue.Name, time.Value));
            }
        }
        return candidates.OrderBy(candidate => candidate.Time).Cast<(string, DateTime)?>().FirstOrDefault();
    }

    /// <summary>
    /// 返回阻止闲时自动更新的首个调度原因。调用方可在宿主维护协调锁内调用，
    /// 以保证 occurrence 注册与维护租约之间没有检查后到登记前的窗口。
    /// </summary>
    public AutoUpdateIdleBlocker? GetAutoUpdateBlocker(TimeSpan horizon, DateTime? nowOverride = null)
    {
        DateTime now = nowOverride ?? DateTime.Now;
        DateTime until = now.Add(horizon < TimeSpan.Zero ? TimeSpan.Zero : horizon);
        IReadOnlyList<DispatchQueue> queues = _queues.Snapshot();
        lock (_sync)
        {
            if (_retryQueue.IsAnyQueueRunning)
            {
                return new AutoUpdateIdleBlocker(
                    "queue_running",
                    "存在正在运行的调度队列",
                    QueueName: null,
                    TriggerTime: null);
            }
            if (_retryQueue.AttemptingCount > 0)
            {
                return new AutoUpdateIdleBlocker(
                    "queue_attempting",
                    "存在正在准入的调度队列",
                    QueueName: null,
                    TriggerTime: null);
            }
            ScheduledOccurrence? pending = _retryQueue
                .SnapshotPending(item => item.Status is "Triggered" or "Waiting")
                .FirstOrDefault();
            if (pending is not null)
            {
                return new AutoUpdateIdleBlocker(
                    pending.Status == "Waiting" ? "queue_waiting" : "queue_pending",
                    pending.Status == "Waiting" ? "存在等待执行的调度队列" : "存在尚未准入的调度队列",
                    pending.QueueName,
                    pending.OriginalTriggerTime);
            }
            if (!_stateFence.StartupRunsIssued && queues.Any(queue => queue.AutoRunMode == "startup" && queue.Tasks.Count > 0))
            {
                return new AutoUpdateIdleBlocker(
                    "queue_startup",
                    "存在尚未触发的启动队列",
                    queues.First(queue => queue.AutoRunMode == "startup" && queue.Tasks.Count > 0).Name,
                    null);
            }
        }

        DateTime from = SchedulerTriggerPlanner.ScheduledScanStart(now);
        foreach (DispatchQueue queue in queues.Where(queue => queue.AutoRunMode == "scheduled" && queue.Tasks.Count > 0))
        {
            foreach ((string occurrenceKey, DateTime triggerTime) in SchedulerTriggerPlanner.EnumerateOccurrences(queue, from, until))
            {
                string key = SchedulerTriggerPlanner.TriggerKey(queue.Id, occurrenceKey);
                lock (_sync)
                {
                    if (_stateFence.Occurrences.TryGetValue(key, out ScheduledOccurrence? occurrence)
                        && occurrence.Status is "Completed" or "Cancelled" or "Invalidated")
                    {
                        continue;
                    }
                }

                if (triggerTime <= now)
                {
                    return new AutoUpdateIdleBlocker(
                        "queue_missed",
                        "存在已到时但尚未处理的调度任务",
                        queue.Name,
                        triggerTime);
                }

                return new AutoUpdateIdleBlocker(
                    "queue_soon",
                    $"调度队列「{queue.Name}」将在 {SchedulerTriggerPlanner.FormatRemaining(triggerTime - now)} 后触发",
                    queue.Name,
                    triggerTime);
            }
        }
        return null;
    }

    /// <summary>计算单个调度队列的下一次定时触发时间（今天之后 7 天内的最近匹配）；非定时模式/无任务/无匹配返回 null。</summary>
    public DateTime? NextTriggerFor(DispatchQueue queue)
    {
        return SchedulerTriggerPlanner.NextTriggerFor(queue, DateTime.Now);
    }

    /// <summary>用户卡片使用的最近定时队列投影；只考虑已启用绑定引用的脚本。</summary>
    public (string QueueName, DateTime TriggerTime)? NextTriggerForUser(NexusUser user)
    {
        return NextTriggerForUser(user, _queues.Snapshot());
    }

    /// <summary>用户卡片使用的最近定时队列投影；只考虑已启用绑定引用的脚本。</summary>
    public (string QueueName, DateTime TriggerTime)? NextTriggerForUser(
        NexusUser user,
        IReadOnlyList<DispatchQueue>? queueSnapshot = null)
    {
        HashSet<string> scriptIds = user.Bindings
            .Select(binding => UserBindingOverrideResolver.Resolve(user, binding))
            .Where(binding => binding.Participates)
            .Select(binding => binding.ScriptInstanceId)
            .ToHashSet(StringComparer.Ordinal);
        if (scriptIds.Count == 0)
        {
            return null;
        }
        DateTime now = DateTime.Now;
        var candidates = new List<(string QueueName, DateTime TriggerTime)>();
        foreach (DispatchQueue queue in (queueSnapshot ?? _queues.Snapshot())
            .Where(item => item.AutoRunMode == "scheduled" && item.Tasks.Any(task => scriptIds.Contains(task.ScriptInstanceId))))
        {
            DateTime? trigger = SchedulerTriggerPlanner.NextTriggerFor(queue, now);
            if (trigger is not null)
            {
                candidates.Add((queue.Name, trigger.Value));
            }
        }
        return candidates.OrderBy(item => item.TriggerTime).Cast<(string, DateTime)?>().FirstOrDefault();
    }

    /// <summary>检测尚未准入的冻结计划是否仍引用用户，供全局用户修改/删除门禁使用。</summary>
    public bool HasPendingUser(string userId)
    {
        lock (_sync)
        {
            return _retryQueue.SnapshotPending(item => item.Status is "Triggered" or "Waiting")
                .Any(item => item.Plan?.Tasks.Any(task => task.ResolvedUsers?.Any(user =>
                    string.Equals(user.UserId, userId, StringComparison.OrdinalIgnoreCase)) == true) == true);
        }
    }

    /// <summary>检测尚未准入的冻结计划是否仍引用指定脚本的指定用户绑定。</summary>
    public bool HasPendingBinding(string userId, string scriptId)
    {
        lock (_sync)
        {
            return _retryQueue.SnapshotPending(item => item.Status is "Triggered" or "Waiting")
                .Any(item => item.Plan?.Tasks.Any(task =>
                    string.Equals(task.Script?.Id ?? task.Task.ScriptInstanceId, scriptId, StringComparison.Ordinal)
                    && task.ResolvedUsers?.Any(user =>
                        string.Equals(user.UserId, userId, StringComparison.OrdinalIgnoreCase)) == true) == true);
        }
    }

    public void Dispose()
    {
        Stop();
    }

    internal void TickForTest()
    {
        Tick();
    }

    internal SchedulerTestSnapshot GetTestSnapshot() => _retryQueue.SnapshotForTest();

    internal void MakePendingTriggersDueForTest() => _retryQueue.MakeAllPendingDueForTest();

    internal string AddPendingForTest(QueueExecutionPlan plan, string queueId, string occurrenceKey)
    {
        var occurrence = new ScheduledOccurrence
        {
            QueueId = queueId,
            QueueName = plan.Queue.Name,
            OccurrenceKey = occurrenceKey,
            OriginalTriggerTime = DateTime.Now,
            Status = "Waiting",
            NextAttemptAt = DateTime.Now.AddHours(1),
            Plan = plan,
        };
        lock (_sync)
        {
            if (!_stateFence.Occurrences.TryAdd(occurrence.Key, occurrence)
                || !_retryQueue.TryAddPending(occurrence))
            {
                throw new InvalidOperationException($"测试 occurrence 已存在：{occurrence.Key}");
            }
            MarkStateDirtyLocked();
        }
        return occurrence.Key;
    }

    internal void RemoveOccurrenceForTest(string key)
    {
        lock (_sync)
        {
            _retryQueue.RemovePending(key);
            _stateFence.Occurrences.Remove(key);
            MarkStateDirtyLocked();
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (true)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 调度器异常：{ex.Message}");
            }
            try
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Tick()
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        if (!string.Equals(_lastCleanupDate, today, StringComparison.Ordinal))
        {
            _lastCleanupDate = today;
            _history.Cleanup(_settings.Current.HistoryRetentionDays);
            DecayRunDays(today);
        }

        List<DispatchQueue> queues = _queues.Snapshot().ToList();
        DateTime now = DateTime.Now;
        RetryPendingTriggers(now, queues);

        if (!_stateFence.StartupRunsIssued)
        {
            lock (_sync)
            {
                _stateFence.MarkStartupRunsIssuedLocked();
            }
            foreach (DispatchQueue queue in queues.Where(queue => queue.AutoRunMode == "startup" && queue.Tasks.Count > 0))
            {
                Audit.Log(Audit.Scheduler, "启动时触发队列", queue.Name);
                EnqueueTrigger(queue, "startup-" + now.ToString("yyyyMMddHHmmssfff"), now, isStartup: true);
            }
        }

        lock (_sync)
        {
            // durable watermark 只作为恢复围栏保存；计划扫描严格限制在当前分钟，
            // 宿主离线或长时间停顿期间错过的 occurrence 不在启动后补发。
            _stateFence.LastSchedulerCheck = now;
        }
        DateTime from = SchedulerTriggerPlanner.ScheduledScanStart(now);
        foreach (DispatchQueue queue in queues.Where(queue => queue.AutoRunMode == "scheduled" && queue.Tasks.Count > 0))
        {
            foreach ((string occurrenceKey, DateTime triggerTime) in SchedulerTriggerPlanner.EnumerateOccurrences(queue, from, now))
            {
                string key = SchedulerTriggerPlanner.TriggerKey(queue.Id, occurrenceKey);
                lock (_sync)
                {
                    if (_stateFence.Occurrences.ContainsKey(key))
                    {
                        continue;
                    }
                }
                Audit.Log(Audit.Scheduler, "定时触发队列", $"{queue.Name}（{triggerTime:HH:mm}）");
                EnqueueTrigger(queue, occurrenceKey, triggerTime, isStartup: false);
            }
        }
        SaveState();
    }

    /// <summary>每日首次 tick：运行天数 &gt; 0 的绑定递减 1（同日重复跳过一次）。</summary>
    private void DecayRunDays(string today)
    {
        if (_runDaysMaintenance is null || string.Equals(_lastRunDaysDecayDate, today, StringComparison.Ordinal))
        {
            return;
        }
        _lastRunDaysDecayDate = today;
        try
        {
            if (_runDaysMaintenance.DecrementDaily())
            {
                Audit.Log(Audit.Scheduler, "运行天数每日递减", "绑定运行天数已递减 1");
                RevalidatePendingPlans();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[调度] 运行天数每日递减失败：{ex.Message}");
        }
    }

    private void EnqueueTrigger(DispatchQueue queue, string occurrenceKey, DateTime originalTriggerTime, bool isStartup)
    {
        if (_coordination is not null)
        {
            _coordination.WithAdmissionCoordination(() =>
            {
                EnqueueTriggerCore(queue, occurrenceKey, originalTriggerTime, isStartup);
                return true;
            });
            return;
        }

        EnqueueTriggerCore(queue, occurrenceKey, originalTriggerTime, isStartup);
    }

    private void EnqueueTriggerCore(DispatchQueue queue, string occurrenceKey, DateTime originalTriggerTime, bool isStartup)
    {
        QueueExecutionPlan? plan = null;
        if (_plans is not null)
        {
            try
            {
                plan = _plans.BuildQueueForSchedule(queue.Id);
            }
            catch (Exception ex)
            {
                var invalid = new ScheduledOccurrence
                {
                    QueueId = queue.Id,
                    QueueName = queue.Name,
                    OccurrenceKey = occurrenceKey,
                    OriginalTriggerTime = originalTriggerTime,
                    IsStartup = isStartup,
                    Status = "Invalidated",
                    LastReason = ex.Message,
                };
                lock (_sync)
                {
                    if (_stateFence.Occurrences.TryAdd(invalid.Key, invalid))
                    {
                        MarkStateDirtyLocked();
                        Logger.Error($"[错误] 自动运行队列「{queue.Name}」触发失败：{ex.Message}");
                    }
                }
                SaveState();
                return;
            }
        }

        var pending = new ScheduledOccurrence
        {
            QueueId = queue.Id,
            QueueName = plan?.Queue.Name ?? queue.Name,
            OccurrenceKey = occurrenceKey,
            OriginalTriggerTime = originalTriggerTime,
            IsStartup = isStartup,
            NextAttemptAt = DateTime.Now,
            Plan = plan,
            Status = "Triggered",
        };
        lock (_sync)
        {
            if (!_stateFence.Occurrences.TryAdd(pending.Key, pending))
            {
                return;
            }
            _retryQueue.TryAddPending(pending);
            MarkStateDirtyLocked();
            if (isStartup)
            {
                _stateFence.MarkStartupRunsIssuedLocked();
            }
        }
        SaveState();
        QueueTriggerAttempt(pending);
    }

    private void RetryPendingTriggers(DateTime now, IReadOnlyList<DispatchQueue> queues)
    {
        Dictionary<string, DispatchQueue> byId = queues.ToDictionary(queue => queue.Id, StringComparer.Ordinal);
        ScheduledOccurrence[] pending = _retryQueue
            .SnapshotPending(item => item.NextAttemptAt <= now && (item.Status is "Triggered" or "Waiting"))
            .ToArray();
        foreach (ScheduledOccurrence item in pending)
        {
            if (!byId.TryGetValue(item.QueueId, out DispatchQueue? queue))
            {
                InvalidatePending(item, $"调度队列不存在：{item.QueueId}", saveHistory: true);
                continue;
            }
            if (!item.IsStartup && !SchedulerTriggerPlanner.MatchesOccurrence(queue, item.OriginalTriggerTime))
            {
                InvalidatePending(item, "定时配置已变化，本次等待触发已取消", saveHistory: false, cancelled: true);
                continue;
            }
            QueueTriggerAttempt(item);
        }
    }

    private void QueueTriggerAttempt(ScheduledOccurrence pending)
    {
        if (!_retryQueue.TryClaimAttempt(pending))
        {
            return;
        }
        _ = Task.Run(() => AttemptTriggerAsync(pending));
    }

    private async Task AttemptTriggerAsync(ScheduledOccurrence pending)
    {
        QueueExecutionPlan? plan = pending.Plan;
        DispatchQueue? queue = plan?.Queue ?? _queues.Snapshot().FirstOrDefault(item => item.Id == pending.QueueId);
        if (queue is null)
        {
            InvalidatePending(pending, $"调度队列不存在：{pending.QueueId}", saveHistory: true);
            _retryQueue.ReleaseAttempt(pending);
            return;
        }

        bool alreadyRunning;
        lock (_sync)
        {
            if (!_retryQueue.TryGetPending(pending.Key, out ScheduledOccurrence? current)
                || !ReferenceEquals(current, pending))
            {
                _retryQueue.ReleaseAttempt(pending);
                return;
            }
            alreadyRunning = _retryQueue.IsQueueRunning(queue.Id);
            if (alreadyRunning)
            {
                _retryQueue.ScheduleRetry(pending, $"队列「{queue.Name}」已有自动运行实例");
                MarkStateDirtyLocked();
                _retryQueue.ReleaseAttempt(pending);
            }
            else
            {
                _retryQueue.MarkQueueRunning(queue.Id);
            }
        }
        if (alreadyRunning)
        {
            SaveState();
            return;
        }

        try
        {
            if (plan is not null)
            {
                string? blocked = _validator.QueueBlockedByPlan(plan);
                if (blocked is not null)
                {
                    ScheduleRetry(pending, $"脚本「{blocked}」进程仍在运行");
                    return;
                }
            }
            else if (_plans is not null)
            {
                plan = _plans.BuildQueueForSchedule(queue.Id);
                lock (_sync)
                {
                    if (_retryQueue.TryGetPending(pending.Key, out ScheduledOccurrence? live)
                        && ReferenceEquals(live, pending))
                    {
                        live.Plan = plan;
                        MarkStateDirtyLocked();
                    }
                }
            }

            RunningExecution exec;
            try
            {
                exec = plan is not null && _commands is IFrozenQueueExecutionService frozen
                    ? frozen.StartQueue(plan, "auto", Audit.Scheduler)
                    : _commands.StartQueue(queue.Id, "auto", Audit.Scheduler);
            }
            catch (ExecutionAdmissionException admission) when (admission.Failure.Disposition == AdmissionFailureDisposition.Transient)
            {
                ScheduleRetry(pending, admission.Failure.Message);
                return;
            }
            catch (ExecutionAdmissionException admission)
            {
                InvalidatePending(pending, admission.Failure.Message, saveHistory: false);
                return;
            }
            catch (Exception ex)
            {
                InvalidatePending(pending, ex.Message, saveHistory: false);
                return;
            }

            lock (_sync)
            {
                _retryQueue.MarkRunning(pending);
                MarkStateDirtyLocked();
            }
            SaveState();
            try
            {
                await exec.Completion.ConfigureAwait(false);
            }
            finally
            {
                lock (_sync)
                {
                    pending.Status = "Completed";
                    MarkStateDirtyLocked();
                }
                SaveState();
            }
        }
        finally
        {
            lock (_sync)
            {
                _retryQueue.ReleaseQueue(queue.Id);
                _retryQueue.ReleaseAttempt(pending);
            }
        }
    }

    private void ScheduleRetry(ScheduledOccurrence pending, string reason)
    {
        lock (_sync)
        {
            if (!_retryQueue.ContainsPending(pending.Key)
                || !_retryQueue.ScheduleRetry(pending, reason))
            {
                return;
            }
            MarkStateDirtyLocked();
        }
        SaveState();
        Logger.Info($"[调度等待] 队列「{pending.QueueName}」本次触发暂缓：{reason}；将在资源释放后重试。");
    }

    private void InvalidatePending(ScheduledOccurrence pending, string reason, bool saveHistory, bool cancelled = false)
    {
        lock (_sync)
        {
            if (!_stateFence.Occurrences.TryGetValue(pending.Key, out ScheduledOccurrence? current)
                || !ReferenceEquals(current, pending))
            {
                return;
            }
            _retryQueue.RemovePending(pending.Key);
            pending.Status = cancelled ? "Cancelled" : "Invalidated";
            pending.LastReason = reason;
            MarkStateDirtyLocked();
        }
        Logger.Error($"[错误] 自动运行队列「{pending.QueueName}」触发失败：{reason}");
        if (saveHistory)
        {
            var skipped = new RunRecord
            {
                ScriptName = pending.QueueName,
                QueueId = pending.QueueId,
                QueueName = pending.QueueName,
                Mode = "auto",
                StartTime = DateTime.Now,
                EndTime = DateTime.Now,
                Status = "failed",
                ResultDetail = reason,
                ResultCode = "scheduler.trigger_failed",
            };
            _history.Save(skipped, new List<string>(), Array.Empty<RunScreenshot>());
        }
        SaveState();
    }

    private void MarkStateDirtyLocked() => _stateFence.MarkDirtyLocked();

    private void SaveState(bool force = false) => _stateFence.Save(force);

}
