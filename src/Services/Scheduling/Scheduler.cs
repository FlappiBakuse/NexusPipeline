using NexusPipeline.Models;
using NexusPipeline.Utilities;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Services.Execution;
namespace NexusPipeline.Services;

internal class Scheduler : IDisposable
{
    private readonly object _sync = new();

    private readonly HashSet<string> _runningQueueIds = new();

    private readonly Dictionary<string, string> _lastTrigger = new();

    private readonly Dictionary<string, PendingScheduledRun> _pendingTriggers = new();

    private readonly HashSet<string> _attemptingTriggers = new();

    private CancellationTokenSource? _cts;

    private Task? _loop;

    private bool _startupRunsIssued;

    private string? _lastCleanupDate;

    private readonly IQueueRepository _queues;

    private readonly IHistoryStore _history;

    private readonly ISettingsProvider _settings;

    private readonly IExecutionService _commands;

    private readonly ExecutionValidator _validator;

    public Scheduler(
        IQueueRepository queues,
        IHistoryStore history,
        ISettingsProvider settings,
        IExecutionService commands,
        ExecutionValidator validator)
    {
        _queues = queues;
        _history = history;
        _settings = settings;
        _commands = commands;
        _validator = validator;
    }

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }
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
        // v0.6.5+：取消后释放 CTS（此前仅 Cancel 不 Dispose，重复 Start 时旧 CTS 泄漏）。
        try
        {
            _cts?.Dispose();
        }
        catch
        {
        }
        _cts = null;
        _loop = null;
    }

    /// <summary>计算下一次定时触发的调度队列（今天之后 7 天内的最近匹配时间点；仅定时模式，不含启动时运行）。</summary>
    public (string QueueName, DateTime TriggerTime)? NextTrigger()
    {
        DateTime now = DateTime.Now;
        var candidates = new List<(string Name, DateTime Time)>();
        // v0.7.2+（KN-04）：锁内快照队列列表，避免与 Web 修改并发冲突（调度线程每秒枚举）。
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
        // 每日清理一次（历史/脚本控制台/管理器日志按保留天数，服务持续运行期间同样生效）。
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        if (!string.Equals(_lastCleanupDate, today, StringComparison.Ordinal))
        {
            _lastCleanupDate = today;
            _history.Cleanup(_settings.Current.HistoryRetentionDays);
        }

        // v0.7.2+（KN-04）：锁内快照队列列表，避免与 Web 请求线程并发修改冲突（调度线程每秒枚举）。
        List<DispatchQueue> queues = _queues.Snapshot().ToList();

        RetryPendingTriggers(DateTime.Now, queues);

        if (!_startupRunsIssued)
        {
            _startupRunsIssued = true;
            foreach (DispatchQueue queue in queues.Where(queue => queue.AutoRunMode == "startup" && queue.Tasks.Count > 0))
            {
                Audit.Log(Audit.Scheduler, "启动时触发队列", queue.Name);
                EnqueueTrigger(queue, "startup", DateTime.Now, isStartup: true);
            }
        }

        DateTime now = DateTime.Now;
        string clock = now.ToString("HH:mm");
        foreach (DispatchQueue queue in queues.Where(queue => queue.AutoRunMode == "scheduled" && queue.Tasks.Count > 0))
        {
            bool hit = queue.TimeSets.Any(timeSet =>
                timeSet.Enabled
                && timeSet.Days.Contains((int)now.DayOfWeek)
                && string.Equals(timeSet.Time, clock, StringComparison.Ordinal));
            if (!hit)
            {
                continue;
            }
            string key = $"{now:yyyy-MM-dd} {clock}";
            string pendingKey = TriggerKey(queue.Id, key);
            lock (_sync)
            {
                if ((_lastTrigger.TryGetValue(queue.Id, out string? last) && last == key)
                    || _pendingTriggers.ContainsKey(pendingKey))
                {
                    continue;
                }
            }
            Audit.Log(Audit.Scheduler, "定时触发队列", $"{queue.Name}（{clock}）");
            EnqueueTrigger(queue, key, now, isStartup: false);
        }
    }

    private void EnqueueTrigger(DispatchQueue queue, string occurrenceKey, DateTime originalTriggerTime, bool isStartup)
    {
        var pending = new PendingScheduledRun
        {
            QueueId = queue.Id,
            QueueName = queue.Name,
            OccurrenceKey = occurrenceKey,
            OriginalTriggerTime = originalTriggerTime,
            IsStartup = isStartup,
            NextAttemptAt = DateTime.Now,
        };
        lock (_sync)
        {
            if (!_pendingTriggers.TryAdd(pending.Key, pending))
            {
                return;
            }
        }
        QueueTriggerAttempt(pending);
    }

    private void RetryPendingTriggers(DateTime now, IReadOnlyList<DispatchQueue> queues)
    {
        Dictionary<string, DispatchQueue> byId = queues.ToDictionary(queue => queue.Id, StringComparer.Ordinal);
        PendingScheduledRun[] pending;
        lock (_sync)
        {
            pending = _pendingTriggers.Values
                .Where(item => item.NextAttemptAt <= now)
                .ToArray();
        }
        foreach (PendingScheduledRun item in pending)
        {
            if (byId.TryGetValue(item.QueueId, out DispatchQueue? queue))
            {
                item.QueueName = queue.Name;
                QueueTriggerAttempt(item);
            }
            else
            {
                CompletePermanentFailure(item, $"调度队列不存在：{item.QueueId}");
            }
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
        DispatchQueue? queue = _queues.Snapshot().FirstOrDefault(item => item.Id == pending.QueueId);
        if (queue is null)
        {
            CompletePermanentFailure(pending, $"调度队列不存在：{pending.QueueId}");
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
                return;
            }
            _runningQueueIds.Add(queue.Id);
        }

        try
        {
            // v0.7.0：长时/普通混排防御（保存时已校验，此处兜底手工改配置/旧数据场景）——永久错误只消费本次触发。
            string? mixError = _validator.CheckQueueMix(queue);
            if (mixError is not null)
            {
                CompletePermanentFailure(pending, mixError, saveHistory: true);
                return;
            }

            RunningExecution exec;
            try
            {
                exec = _commands.StartQueue(queue.Id, "auto", Audit.Scheduler);
            }
            catch (ExecutionAdmissionException admission) when (admission.Failure.Disposition == AdmissionFailureDisposition.Transient)
            {
                ScheduleRetry(pending, admission.Failure.Message);
                return;
            }
            catch (ExecutionAdmissionException admission)
            {
                CompletePermanentFailure(pending, admission.Failure.Message, saveHistory: false);
                return;
            }
            catch (Exception ex)
            {
                CompletePermanentFailure(pending, ex.Message, saveHistory: false);
                return;
            }

            lock (_sync)
            {
                _pendingTriggers.Remove(pending.Key);
                _lastTrigger[queue.Id] = pending.OccurrenceKey;
            }
            await exec.Completion.ConfigureAwait(false);
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
        Logger.Info($"[调度等待] 队列「{pending.QueueName}」本次触发暂缓：{reason}；将在资源释放后重试。");
    }

    private static void ScheduleRetryLocked(PendingScheduledRun pending, string reason)
    {
        pending.RetryCount++;
        pending.LastReason = reason;
        pending.NextAttemptAt = DateTime.Now.AddSeconds(TestHooks.ScaledSeconds(5));
    }

    private void CompletePermanentFailure(PendingScheduledRun pending, string reason, bool saveHistory = false)
    {
        lock (_sync)
        {
            if (!_pendingTriggers.Remove(pending.Key))
            {
                return;
            }
            _lastTrigger[pending.QueueId] = pending.OccurrenceKey;
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

        public int RetryCount { get; set; }

        public string LastReason { get; set; } = "";

        public DateTime NextAttemptAt { get; set; }

        public string Key => TriggerKey(QueueId, OccurrenceKey);
    }
}
