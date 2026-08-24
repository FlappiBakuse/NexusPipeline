using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

internal sealed class Scheduler : IDisposable
{
    private readonly object _sync = new();

    private readonly HashSet<string> _runningQueueIds = new();

    /// <summary>当前进程内仍待准入的 occurrence；已完成 occurrence 保留在 _occurrences 中用于去重。</summary>
    private readonly Dictionary<string, PendingScheduledRun> _pendingTriggers = new();

    private readonly Dictionary<string, PendingScheduledRun> _occurrences = new();

    private readonly HashSet<string> _attemptingTriggers = new();

    private CancellationTokenSource? _cts;

    private Task? _loop;

    private bool _startupRunsIssued;

    private string? _lastCleanupDate;

    private DateTime? _lastSchedulerCheck;

    private readonly IQueueRepository _queues;

    private readonly IHistoryStore _history;

    private readonly ISettingsProvider _settings;

    private readonly IExecutionService _commands;

    private readonly ExecutionValidator _validator;

    private readonly ExecutionPlanBuilder? _plans;

    private readonly ISchedulerStateStore _stateStore;

    private readonly IUserRunDaysWriter? _runDaysWriter;

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
        IUserRunDaysWriter? runDaysWriter = null)
    {
        _queues = queues;
        _history = history;
        _settings = settings;
        _commands = commands;
        _validator = validator;
        _plans = plans;
        _stateStore = stateStore ?? new MemorySchedulerStateStore();
        _runDaysWriter = runDaysWriter;
        // 启动当天不立即递减（避免每次重启就少一天）；只有运行期间跨天、或首次 tick 在启动后的次日触发才递减。
        _lastRunDaysDecayDate = DateTime.Now.ToString("yyyy-MM-dd");
        RestorePersistedState();
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
        SaveState();
    }

    /// <summary>用户修改队列、脚本或用户后调用，显式重校验尚未准入的冻结计划。</summary>
    public void RevalidatePendingPlans()
    {
        PendingScheduledRun[] pending;
        lock (_sync)
        {
            pending = _pendingTriggers.Values
                .Where(item => item.Status is "Triggered" or "Waiting")
                .ToArray();
        }

        foreach (PendingScheduledRun item in pending)
        {
            DispatchQueue? current = _queues.Snapshot().FirstOrDefault(queue => queue.Id == item.QueueId);
            if (current is null)
            {
                InvalidatePending(item, "引用的调度队列已删除", saveHistory: false);
                continue;
            }
            if (!item.IsStartup && !MatchesOccurrence(current, item.OriginalTriggerTime))
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
                    if (_pendingTriggers.TryGetValue(item.Key, out PendingScheduledRun? live)
                        && ReferenceEquals(live, item)
                        && (live.Status is "Triggered" or "Waiting"))
                    {
                        live.Plan = plan;
                        live.QueueName = plan.Queue.Name;
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
            DateTime? time = NextTriggerFor(queue, now);
            if (time is not null)
            {
                candidates.Add((queue.Name, time.Value));
            }
        }
        return candidates.OrderBy(candidate => candidate.Time).Cast<(string, DateTime)?>().FirstOrDefault();
    }

    /// <summary>计算单个调度队列的下一次定时触发时间（今天之后 7 天内的最近匹配）；非定时模式/无任务/无匹配返回 null。</summary>
    public DateTime? NextTriggerFor(DispatchQueue queue)
    {
        return NextTriggerFor(queue, DateTime.Now);
    }

    /// <summary>用户卡片使用的最近定时队列投影；只考虑已启用绑定引用的脚本。</summary>
    public (string QueueName, DateTime TriggerTime)? NextTriggerForUser(
        NexusUser user,
        IReadOnlyList<DispatchQueue>? queueSnapshot = null)
    {
        HashSet<string> scriptIds = user.Bindings
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
            DateTime? trigger = NextTriggerFor(queue, now);
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
            return _pendingTriggers.Values
                .Where(item => item.Status is "Triggered" or "Waiting")
                .Any(item => item.Plan?.Tasks.Any(task => task.ResolvedUsers?.Any(user =>
                    string.Equals(user.UserId, userId, StringComparison.OrdinalIgnoreCase)) == true) == true);
        }
    }

    private static DateTime? NextTriggerFor(DispatchQueue queue, DateTime now)
    {
        if (queue.AutoRunMode != "scheduled" || queue.Tasks.Count == 0)
        {
            return null;
        }
        var candidates = new List<DateTime>();
        foreach (QueueTimeSet timeSet in queue.TimeSets.Where(timeSet => timeSet.Enabled))
        {
            if (!TimeOnly.TryParseExact(timeSet.Time, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out TimeOnly timeOnly))
            {
                continue;
            }
            for (int offset = 0; offset < 7; offset++)
            {
                DateTime candidate = now.Date.AddDays(offset).Add(timeOnly.ToTimeSpan());
                if (candidate > now && timeSet.Days.Contains((int)candidate.DayOfWeek))
                {
                    candidates.Add(candidate);
                    break;
                }
            }
        }
        return candidates.Count == 0 ? null : candidates.Min();
    }

    public void Dispose()
    {
        Stop();
    }

    internal void TickForTest()
    {
        Tick();
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

        if (!_startupRunsIssued)
        {
            _startupRunsIssued = true;
            foreach (DispatchQueue queue in queues.Where(queue => queue.AutoRunMode == "startup" && queue.Tasks.Count > 0))
            {
                Audit.Log(Audit.Scheduler, "启动时触发队列", queue.Name);
                EnqueueTrigger(queue, "startup-" + now.ToString("yyyyMMddHHmmssfff"), now, isStartup: true);
            }
        }

        DateTime from;
        lock (_sync)
        {
            // 首次 tick 仍检查当前分钟，后续按 (lastCheck, now] 补齐整个停顿窗口。
            from = _lastSchedulerCheck ?? now.AddMinutes(-1);
            _lastSchedulerCheck = now;
        }
        foreach (DispatchQueue queue in queues.Where(queue => queue.AutoRunMode == "scheduled" && queue.Tasks.Count > 0))
        {
            foreach ((string occurrenceKey, DateTime triggerTime) in EnumerateOccurrences(queue, from, now))
            {
                string key = TriggerKey(queue.Id, occurrenceKey);
                lock (_sync)
                {
                    if (_occurrences.ContainsKey(key))
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
        if (_runDaysWriter is null || string.Equals(_lastRunDaysDecayDate, today, StringComparison.Ordinal))
        {
            return;
        }
        _lastRunDaysDecayDate = today;
        try
        {
            if (_runDaysWriter.DecrementDaily())
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
        QueueExecutionPlan? plan = null;
        if (_plans is not null)
        {
            try
            {
                plan = _plans.BuildQueueForSchedule(queue.Id);
            }
            catch (Exception ex)
            {
                var invalid = new PendingScheduledRun
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
                    if (_occurrences.TryAdd(invalid.Key, invalid))
                    {
                        Logger.Error($"[错误] 自动运行队列「{queue.Name}」触发失败：{ex.Message}");
                    }
                }
                SaveState();
                return;
            }
        }

        var pending = new PendingScheduledRun
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
            if (!_occurrences.TryAdd(pending.Key, pending))
            {
                return;
            }
            _pendingTriggers[pending.Key] = pending;
            if (isStartup)
            {
                _startupRunsIssued = true;
            }
        }
        SaveState();
        QueueTriggerAttempt(pending);
    }

    private void RetryPendingTriggers(DateTime now, IReadOnlyList<DispatchQueue> queues)
    {
        Dictionary<string, DispatchQueue> byId = queues.ToDictionary(queue => queue.Id, StringComparer.Ordinal);
        PendingScheduledRun[] pending;
        lock (_sync)
        {
            pending = _pendingTriggers.Values
                .Where(item => item.NextAttemptAt <= now && (item.Status is "Triggered" or "Waiting"))
                .ToArray();
        }
        foreach (PendingScheduledRun item in pending)
        {
            if (!byId.TryGetValue(item.QueueId, out DispatchQueue? queue))
            {
                InvalidatePending(item, $"调度队列不存在：{item.QueueId}", saveHistory: true);
                continue;
            }
            if (!item.IsStartup && !MatchesOccurrence(queue, item.OriginalTriggerTime))
            {
                InvalidatePending(item, "定时配置已变化，本次等待触发已取消", saveHistory: false, cancelled: true);
                continue;
            }
            QueueTriggerAttempt(item);
        }
    }

    private void QueueTriggerAttempt(PendingScheduledRun pending)
    {
        lock (_sync)
        {
            if (!_pendingTriggers.TryGetValue(pending.Key, out PendingScheduledRun? current)
                || !ReferenceEquals(current, pending)
                || pending.NextAttemptAt > DateTime.Now
                || !_attemptingTriggers.Add(pending.Key))
            {
                return;
            }
        }
        _ = Task.Run(() => AttemptTriggerAsync(pending));
    }

    private async Task AttemptTriggerAsync(PendingScheduledRun pending)
    {
        QueueExecutionPlan? plan = pending.Plan;
        DispatchQueue? queue = plan?.Queue ?? _queues.Snapshot().FirstOrDefault(item => item.Id == pending.QueueId);
        if (queue is null)
        {
            InvalidatePending(pending, $"调度队列不存在：{pending.QueueId}", saveHistory: true);
            ReleaseTriggerAttempt(pending);
            return;
        }

        lock (_sync)
        {
            if (!_pendingTriggers.TryGetValue(pending.Key, out PendingScheduledRun? current)
                || !ReferenceEquals(current, pending))
            {
                ReleaseTriggerAttemptLocked(pending);
                return;
            }
            if (_runningQueueIds.Contains(queue.Id))
            {
                ScheduleRetryLocked(pending, $"队列「{queue.Name}」已有自动运行实例");
                ReleaseTriggerAttemptLocked(pending);
                SaveState();
                return;
            }
            _runningQueueIds.Add(queue.Id);
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
                pending.Plan = plan;
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
                _pendingTriggers.Remove(pending.Key);
                pending.Status = "Running";
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
                }
                SaveState();
            }
        }
        finally
        {
            lock (_sync)
            {
                _runningQueueIds.Remove(queue.Id);
                ReleaseTriggerAttemptLocked(pending);
            }
        }
    }

    private void ScheduleRetry(PendingScheduledRun pending, string reason)
    {
        lock (_sync)
        {
            if (!_pendingTriggers.ContainsKey(pending.Key))
            {
                return;
            }
            ScheduleRetryLocked(pending, reason);
        }
        SaveState();
        Logger.Info($"[调度等待] 队列「{pending.QueueName}」本次触发暂缓：{reason}；将在资源释放后重试。");
    }

    private static void ScheduleRetryLocked(PendingScheduledRun pending, string reason)
    {
        pending.Status = "Waiting";
        pending.RetryCount++;
        pending.LastReason = reason;
        pending.NextAttemptAt = DateTime.Now.AddSeconds(TestHooks.ScaledSeconds(5));
    }

    private void InvalidatePending(PendingScheduledRun pending, string reason, bool saveHistory, bool cancelled = false)
    {
        lock (_sync)
        {
            if (!_occurrences.TryGetValue(pending.Key, out PendingScheduledRun? current)
                || !ReferenceEquals(current, pending))
            {
                return;
            }
            _pendingTriggers.Remove(pending.Key);
            pending.Status = cancelled ? "Cancelled" : "Invalidated";
            pending.LastReason = reason;
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
                FinalStatus = "failed",
                ResultDetail = reason,
            };
            _history.Save(skipped, new List<string>());
        }
        SaveState();
    }

    private void ReleaseTriggerAttempt(PendingScheduledRun pending)
    {
        lock (_sync)
        {
            ReleaseTriggerAttemptLocked(pending);
        }
    }

    private void ReleaseTriggerAttemptLocked(PendingScheduledRun pending)
    {
        _attemptingTriggers.Remove(pending.Key);
    }

    private void RestorePersistedState()
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
        _lastSchedulerCheck = state.LastSchedulerCheck;
        lock (_sync)
        {
            foreach (PersistedScheduledOccurrence item in state.Occurrences)
            {
                if (string.IsNullOrWhiteSpace(item.QueueId) || string.IsNullOrWhiteSpace(item.OccurrenceKey))
                {
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
                var occurrence = new PendingScheduledRun
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
                _occurrences[occurrence.Key] = occurrence;
                if (occurrence.Status is "Triggered" or "Waiting")
                {
                    _pendingTriggers[occurrence.Key] = occurrence;
                }
                if (occurrence.IsStartup && (occurrence.Status is "Triggered" or "Waiting" or "Running"))
                {
                    _startupRunsIssued = true;
                }
            }
        }
    }

    private void SaveState()
    {
        SchedulerPersistedState snapshot;
        lock (_sync)
        {
            snapshot = new SchedulerPersistedState
            {
                LastSchedulerCheck = _lastSchedulerCheck,
                Occurrences = _occurrences.Values.Select(item => new PersistedScheduledOccurrence
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
                }).ToList(),
            };
        }
        try
        {
            _stateStore.Save(snapshot);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[调度] 保存 scheduler-state.json 失败：{ex.Message}");
        }
    }

    private static IEnumerable<(string OccurrenceKey, DateTime TriggerTime)> EnumerateOccurrences(
        DispatchQueue queue,
        DateTime from,
        DateTime to)
    {
        for (DateTime date = from.Date; date <= to.Date; date = date.AddDays(1))
        {
            foreach (QueueTimeSet timeSet in queue.TimeSets.Where(item => item.Enabled))
            {
                if (!TimeOnly.TryParseExact(timeSet.Time, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out TimeOnly timeOnly))
                {
                    continue;
                }
                DateTime candidate = date.Add(timeOnly.ToTimeSpan());
                if (candidate > from && candidate <= to && timeSet.Days.Contains((int)candidate.DayOfWeek))
                {
                    yield return ($"{candidate:yyyy-MM-dd HH:mm}", candidate);
                }
            }
        }
    }

    private static bool MatchesOccurrence(DispatchQueue queue, DateTime triggerTime)
    {
        return queue.AutoRunMode == "scheduled"
            && queue.Tasks.Count > 0
            && queue.TimeSets.Any(timeSet =>
                timeSet.Enabled
                && timeSet.Days.Contains((int)triggerTime.DayOfWeek)
                && string.Equals(timeSet.Time, triggerTime.ToString("HH:mm"), StringComparison.Ordinal));
    }

    private static string TriggerKey(string queueId, string occurrenceKey)
    {
        return $"{queueId}\n{occurrenceKey}";
    }

    private sealed class PendingScheduledRun
    {
        public string QueueId { get; init; } = "";

        public string QueueName { get; set; } = "";

        public string OccurrenceKey { get; init; } = "";

        public DateTime OriginalTriggerTime { get; init; }

        public bool IsStartup { get; init; }

        public string Status { get; set; } = "Triggered";

        public int RetryCount { get; set; }

        public string LastReason { get; set; } = "";

        public DateTime NextAttemptAt { get; set; }

        public QueueExecutionPlan? Plan { get; set; }

        public string Key => TriggerKey(QueueId, OccurrenceKey);
    }
}
