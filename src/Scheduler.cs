namespace NexusPipeline;

internal class Scheduler : IDisposable
{
    private readonly object _sync = new();

    private readonly HashSet<string> _runningQueueIds = new();

    private readonly Dictionary<string, string> _lastTrigger = new();

    private CancellationTokenSource? _cts;

    private Task? _loop;

    private bool _startupRunsIssued;

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
        _loop = null;
    }

    /// <summary>计算下一次定时触发的调度队列（今天之后 7 天内的最近匹配时间点；仅定时模式，不含启动时运行）。</summary>
    public (string QueueName, DateTime TriggerTime)? NextTrigger()
    {
        DateTime now = DateTime.Now;
        var candidates = new List<(string Name, DateTime Time)>();
        foreach (DispatchQueue queue in RuntimeContext.Instance.Queues.Where(queue => queue.AutoRunMode == "scheduled" && queue.Tasks.Count > 0))
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
        if (!_startupRunsIssued)
        {
            _startupRunsIssued = true;
            foreach (DispatchQueue queue in RuntimeContext.Instance.Queues.Where(queue => queue.AutoRunMode == "startup" && queue.Tasks.Count > 0))
            {
                Audit.Log(Audit.Scheduler, "启动时触发队列", queue.Name);
                TriggerQueue(queue);
            }
        }

        DateTime now = DateTime.Now;
        string clock = now.ToString("HH:mm");
        foreach (DispatchQueue queue in RuntimeContext.Instance.Queues.Where(queue => queue.AutoRunMode == "scheduled" && queue.Tasks.Count > 0))
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
            if (_lastTrigger.TryGetValue(queue.Id, out string? last) && last == key)
            {
                continue;
            }
            _lastTrigger[queue.Id] = key;
            Audit.Log(Audit.Scheduler, "定时触发队列", $"{queue.Name}（{clock}）");
            TriggerQueue(queue);
        }
    }

    private void TriggerQueue(DispatchQueue queue)
    {
        string? blocked = DispatchCenter.QueueBlockedBy(queue);
        if (blocked is not null)
        {
            Logger.Error($"[错误] 自动运行队列「{queue.Name}」检测到脚本「{blocked}」正在运行，已跳过该队列。");
            var skipped = new RunRecord
            {
                ScriptName = queue.Name,
                QueueId = queue.Id,
                QueueName = queue.Name,
                Mode = "auto",
                StartTime = DateTime.Now,
                EndTime = DateTime.Now,
                Status = "failed",
                FinalStatus = "failed",
                ResultDetail = $"检测到脚本「{blocked}」正在运行，已跳过该队列",
            };
            RuntimeContext.Instance.History.Save(skipped, "");
            return;
        }
        lock (_sync)
        {
            if (_runningQueueIds.Contains(queue.Id))
            {
                Logger.Info($"[提示] 调度队列「{queue.Name}」正在运行，跳过本次触发。");
                return;
            }
            _runningQueueIds.Add(queue.Id);
        }
        _ = Task.Run(async () =>
        {
            RunningExecution? exec = null;
            try
            {
                exec = RuntimeContext.Instance.Center.StartQueue(queue.Id, "auto", Audit.Scheduler);
                await exec.Completion.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error($"[错误] 自动运行队列「{queue.Name}」触发失败：{ex.Message}");
            }
            finally
            {
                lock (_sync)
                {
                    _runningQueueIds.Remove(queue.Id);
                }
            }
        });
    }
}
