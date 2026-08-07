using NexusPipeline.Plugins;

namespace NexusPipeline;

internal class RunningExecution
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Kind { get; set; } = "";

    public string TargetId { get; set; } = "";

    public string TargetName { get; set; } = "";

    public string Mode { get; set; } = "";

    public string Status { get; set; } = "running";

    public DateTime StartedAt { get; set; } = DateTime.Now;

    public DateTime? FinishedAt { get; set; }

    public int TotalTasks { get; set; }

    public int DoneTasks { get; set; }

    public string CurrentScriptName { get; set; } = "";

    public string CurrentStatus { get; set; } = "";

    public int CurrentAttempt { get; set; }

    public int CurrentMaxAttempts { get; set; }

    public List<RunRecord> Records { get; set; } = new();

    public CancellationTokenSource Cts { get; set; } = new();

    public Task Completion { get; set; } = Task.CompletedTask;

    private readonly object _logSync = new();

    private readonly List<string> _logLines = new();

    public void AppendLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }
        lock (_logSync)
        {
            _logLines.Add(line);
            if (_logLines.Count > 100)
            {
                _logLines.RemoveRange(0, _logLines.Count - 100);
            }
        }
    }

    public List<string> LogTail(int max = 60)
    {
        lock (_logSync)
        {
            return _logLines.TakeLast(max).ToList();
        }
    }
}

internal class DispatchCenter
{
    private readonly List<RunningExecution> _active = new();

    private readonly object _sync = new();

    public IReadOnlyList<RunningExecution> Active
    {
        get
        {
            lock (_sync)
            {
                return _active.ToList();
            }
        }
    }

    public RunningExecution? Find(string id)
    {
        lock (_sync)
        {
            return _active.FirstOrDefault(exec => exec.Id == id);
        }
    }

    public RunningExecution StartScript(string scriptId, string mode, string source = Audit.System, string? userName = null)
    {
        ScriptInstance? script = RuntimeContext.Instance.FindScript(scriptId);
        if (script is null)
        {
            throw new InvalidOperationException($"脚本实例不存在：{scriptId}");
        }
        if (!string.IsNullOrWhiteSpace(userName) && !string.IsNullOrWhiteSpace(script.ConfigPath)
            && UserConfigManager.FindEnabledUser(script, userName) is null)
        {
            throw new InvalidOperationException($"用户「{userName}」不存在或已禁用");
        }
        var exec = new RunningExecution
        {
            Kind = "script",
            TargetId = script.Id,
            TargetName = script.Name,
            Mode = mode,
            TotalTasks = 1,
            CurrentScriptName = script.Name,
        };
        Register(exec, source);
        exec.CurrentStatus = "排队等待中...";
        Task task = Task.Run(async () =>
        {
            SemaphoreSlim gate = ScriptConfigGate.Get(script.Id);
            try
            {
                await gate.WaitAsync(exec.Cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                exec.Status = "cancelled";
                exec.FinishedAt = DateTime.Now;
                Unregister(exec);
                return;
            }
            try
            {
                await RunScriptAsync(exec, script, userName).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });
        exec.Completion = task;
        return exec;
    }

    public RunningExecution StartQueue(string queueId, string mode, string source = Audit.System)
    {
        DispatchQueue? queue = RuntimeContext.Instance.FindQueue(queueId);
        if (queue is null)
        {
            throw new InvalidOperationException($"调度队列不存在：{queueId}");
        }
        int totalTasks = 0;
        foreach (QueueTask queueTask in queue.Tasks)
        {
            ScriptInstance? script = RuntimeContext.Instance.FindScript(queueTask.ScriptInstanceId);
            totalTasks += script is null ? 1 : Math.Max(1, script.Users.Count(user => user.Enabled));
        }
        var exec = new RunningExecution
        {
            Kind = "queue",
            TargetId = queue.Id,
            TargetName = queue.Name,
            Mode = mode,
            TotalTasks = totalTasks,
        };
        Register(exec, source);
        Task task = Task.Run(() => RunQueueAsync(exec, queue));
        exec.Completion = task;
        return exec;
    }

    public void Cancel(string runId, string source = Audit.System)
    {
        RunningExecution? exec = Find(runId);
        if (exec is null)
        {
            throw new InvalidOperationException($"未找到运行中的任务：{runId}");
        }
        Audit.Log(source, $"取消运行{ExecKindText(exec)}", exec.TargetName);
        try
        {
            exec.Cts.Cancel();
        }
        catch
        {
        }
    }

    private static string ExecKindText(RunningExecution exec)
    {
        return exec.Kind == "queue" ? "调度队列" : "脚本实例";
    }

    private void Register(RunningExecution exec, string source)
    {
        lock (_sync)
        {
            _active.Add(exec);
        }
        Audit.Log(source, $"执行{ExecKindText(exec)}", $"{exec.TargetName}（模式：{(exec.Mode == "auto" ? "自动" : "手动")}）");
    }

    private void Unregister(RunningExecution exec)
    {
        lock (_sync)
        {
            _active.Remove(exec);
        }
    }

    private async Task RunScriptAsync(RunningExecution exec, ScriptInstance script, string? userName = null)
    {
        try
        {
            var session = new RunSession(
                script, exec.Mode, "", "", userName,
                exec.Cts.Token,
                (attempt, max) =>
                {
                    exec.CurrentAttempt = attempt;
                    exec.CurrentMaxAttempts = max;
                },
                status => exec.CurrentStatus = status,
                line => exec.AppendLog(line));

            RunRecord record = await session.RunAsync().ConfigureAwait(false);
            exec.Records.Add(record);
            exec.DoneTasks = 1;
            exec.CurrentStatus = record.Status == "success" ? "运行成功" : (record.Status == "cancelled" ? "已取消" : "运行失败");
            exec.Status = record.Status == "cancelled" ? "cancelled" : "done";
            Logger.Info($"[{(exec.Mode == "auto" ? "自动" : "手动")}运行] 脚本「{script.Name}」最终结果：{record.Status}（{record.ResultDetail}）");

            RuntimeContext.Instance.History.Save(record, session.ScriptLog);
            if (script.NotifyEnabled)
            {
                await RuntimeContext.Instance.Plugins.NotifyScriptAsync(script, record).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            exec.Status = "error";
            Logger.Error($"[错误] 脚本「{script.Name}」运行异常：{ex}");
        }
        finally
        {
            exec.FinishedAt = DateTime.Now;
            Unregister(exec);
        }
    }

    private async Task RunQueueAsync(RunningExecution exec, DispatchQueue queue)
    {
        try
        {
            var records = new List<RunRecord>();
            List<QueueTask> tasks = queue.Tasks.OrderBy(task => task.Index).ToList();
            for (int i = 0; i < tasks.Count; i++)
            {
                if (exec.Cts.IsCancellationRequested)
                {
                    exec.Status = "cancelled";
                    Logger.Info($"调度队列「{queue.Name}」已被取消，后续任务不再执行。");
                    break;
                }
                QueueTask task = tasks[i];
                ScriptInstance? script = RuntimeContext.Instance.FindScript(task.ScriptInstanceId);
                if (script is null)
                {
                    var missing = new RunRecord
                    {
                        ScriptInstanceId = task.ScriptInstanceId,
                        ScriptName = "(脚本实例不存在)",
                        QueueId = queue.Id,
                        QueueName = queue.Name,
                        Mode = exec.Mode,
                        StartTime = DateTime.Now,
                        EndTime = DateTime.Now,
                        Status = "failed",
                        ResultDetail = "脚本实例不存在或已被删除",
                    };
                    records.Add(missing);
                    exec.Records.Add(missing);
                    exec.DoneTasks++;
                    RuntimeContext.Instance.History.Save(missing, "");
                    Logger.Warn($"[警告] 调度队列「{queue.Name}」第 {i + 1} 项引用的脚本实例不存在，已跳过。");
                    continue;
                }

                exec.CurrentScriptName = script.Name;
                exec.CurrentAttempt = 0;
                exec.CurrentStatus = "等待开始";

                List<string?> runUsers = script.Users.Where(user => user.Enabled).Select(user => user.Name).Cast<string?>().ToList();
                if (runUsers.Count == 0)
                {
                    runUsers.Add(null);
                }
                foreach (string? runUser in runUsers)
                {
                    if (exec.Cts.IsCancellationRequested)
                    {
                        exec.Status = "cancelled";
                        Logger.Info($"调度队列「{queue.Name}」已被取消，后续执行不再进行。");
                        break;
                    }
                    string displayName = runUser is null ? script.Name : $"{script.Name}（{runUser}）";
                    exec.CurrentScriptName = displayName;
                    exec.CurrentStatus = "等待开始";

                    SemaphoreSlim gate = ScriptConfigGate.Get(script.Id);
                    try
                    {
                        await gate.WaitAsync(exec.Cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        exec.Status = "cancelled";
                        Logger.Info($"调度队列「{queue.Name}」已在等待脚本「{displayName}」期间被取消。");
                        break;
                    }
                    try
                    {
                        var session = new RunSession(
                            script, exec.Mode, queue.Id, queue.Name, runUser,
                            exec.Cts.Token,
                            (attempt, max) =>
                            {
                                exec.CurrentAttempt = attempt;
                                exec.CurrentMaxAttempts = max;
                            },
                            status => exec.CurrentStatus = status,
                            line => exec.AppendLog(line));

                        RunRecord record = await session.RunAsync().ConfigureAwait(false);
                        records.Add(record);
                        exec.Records.Add(record);
                        exec.DoneTasks++;
                        exec.CurrentStatus = record.Status == "success" ? "运行成功" : (record.Status == "cancelled" ? "已取消" : "运行失败");
                        Logger.Info($"[{(exec.Mode == "auto" ? "自动" : "手动")}运行] 队列「{queue.Name}」第 {i + 1}/{tasks.Count} 项「{displayName}」最终结果：{record.Status}（{record.ResultDetail}）");
                        RuntimeContext.Instance.History.Save(record, session.ScriptLog);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
                if (exec.Status == "cancelled")
                {
                    break;
                }
            }
            if (exec.Status != "cancelled")
            {
                exec.Status = "done";
            }

            bool anyCancelled = records.Any(record => record.Status == "cancelled");
            if (queue.NotifyEnabled)
            {
                await RuntimeContext.Instance.Plugins.NotifyQueueAsync(queue, records).ConfigureAwait(false);
            }
            else if (!queue.NotifyEnabled)
            {
                foreach (RunRecord record in records)
                {
                    ScriptInstance? script = RuntimeContext.Instance.FindScript(record.ScriptInstanceId);
                    if (script is not null && script.NotifyEnabled)
                    {
                        await RuntimeContext.Instance.Plugins.NotifyScriptAsync(script, record).ConfigureAwait(false);
                    }
                }
            }

            if (!anyCancelled)
            {
                Logger.Info($"调度队列「{queue.Name}」全部任务执行完毕，执行完成操作：{QueueRule.CompletionActionDesc(queue.CompletionAction)}。");
                switch (queue.CompletionAction)
                {
                    case "exit":
                        SystemActions.ExitApp();
                        break;
                    case "sleep":
                        SystemActions.Hibernate();
                        break;
                    case "reboot":
                        SystemActions.Reboot();
                        break;
                    case "shutdown":
                        SystemActions.Shutdown();
                        break;
                }
            }
            else
            {
                Logger.Warn($"调度队列「{queue.Name}」未全部完成（有任务被取消），跳过完成操作。");
            }
        }
        catch (Exception ex)
        {
            exec.Status = "error";
            Logger.Error($"[错误] 调度队列「{queue.Name}」运行异常：{ex}");
        }
        finally
        {
            exec.FinishedAt = DateTime.Now;
            Unregister(exec);
        }
    }
}
