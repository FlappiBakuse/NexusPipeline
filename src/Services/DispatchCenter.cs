using NexusPipeline.Models;
using NexusPipeline.Services.Execution;
using NexusPipeline.Services.Notification;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

internal class DispatchCenter
{
    private readonly ExecutionStateStore _state = new();
    private readonly NotificationDispatcher _notifications;

    internal DispatchCenter(NotificationDispatcher notifications)
    {
        _notifications = notifications;
    }

    public IReadOnlyList<RunningExecution> Active
    {
        get => _state.Active;
    }

    public RunningExecution? Find(string id)
    {
        return _state.Find(id);
    }

    /// <summary>查找运行任务（v0.6.3+）：先查运行中列表，再查已结束列表（CLI 轮询结果用；Find 保持只查运行中）。</summary>
    public RunningExecution? FindAny(string id)
    {
        return _state.FindAny(id);
    }

    /// <summary>当前待执行的系统操作（锁内返回拷贝，供 /api/status 展示倒计时卡片）；无则 null。</summary>
    public PendingSystemAction? CurrentSystemAction => _state.CurrentSystemAction;

    /// <summary>取消待执行的系统操作：sleep 取消应用内延迟；reboot/shutdown 执行 shutdown /a 取消 Windows 倒计时。返回是否取消成功。</summary>
    public bool CancelSystemAction()
    {
        if (!_state.TryTakePending(out PendingSystemAction? pending) || pending is null)
        {
            return false;
        }
        string action = pending.Action;
        string queueName = pending.QueueName;
        try
        {
            if (action == "sleep")
            {
                pending.Cts.Cancel();
            }
            else if (action is "reboot" or "shutdown")
            {
                SystemActions.CancelShutdown();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 取消系统操作「{action}」失败：{ex.Message}");
        }
        Audit.Log(Audit.Web, "取消系统操作", $"{action}（{queueName}）");
        return true;
    }

    public RunningExecution StartScript(string scriptId, string mode, string source = Audit.System, string? userName = null)
    {
        ScriptInstance? script = RuntimeContext.Instance.FindScript(scriptId);
        if (script is null)
        {
            throw new InvalidOperationException($"脚本实例不存在：{scriptId}");
        }
        if (IsScriptRunning(script))
        {
            throw new InvalidOperationException($"脚本「{script.Name}」正在运行，请先退出后再执行");
        }
        if (!string.IsNullOrWhiteSpace(userName) && !string.IsNullOrWhiteSpace(script.ConfigPath)
            && UserConfigManager.FindEnabledUser(script, userName) is null)
        {
            throw new InvalidOperationException($"用户「{userName}」不存在或已禁用");
        }
        if (string.IsNullOrWhiteSpace(userName) && !script.Users.Any(user => user.Enabled))
        {
            throw new InvalidOperationException($"脚本「{script.Name}」未配置启用用户，无法运行");
        }
        int totalTasks;
        lock (RuntimeContext.Instance.DataLock)
        {
            // v0.7.2+（KN-04）：锁内读取用户集合，避免与 Web 请求线程的并发修改冲突。
            totalTasks = string.IsNullOrWhiteSpace(userName)
                ? Math.Max(1, script.Users.Count(user => user.Enabled))
                : 1;
        }
        var exec = new RunningExecution
        {
            Kind = "script",
            TargetId = script.Id,
            TargetName = script.Name,
            Mode = mode,
            TotalTasks = totalTasks,
            CurrentScriptName = script.Name,
        };
        Register(exec, source);
        exec.CurrentStatus = "排队等待中...";
        Task task = Task.Run(() => RunScriptAsync(exec, script, userName));
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
        // v0.7.0：长时/普通混排运行期防御（保存时已校验，此处兜底手工改配置/旧数据场景）。
        string? mixError = Limits.CheckQueueMix(RuntimeContext.Instance, queue);
        if (mixError is not null)
        {
            throw new InvalidOperationException(mixError);
        }
        int totalTasks = 0;
        foreach (QueueTask queueTask in queue.Tasks)
        {
            ScriptInstance? script;
            lock (RuntimeContext.Instance.DataLock)
            {
                // v0.7.2+（KN-04）：锁内读取，避免与 Web 请求线程的并发修改冲突。
                script = RuntimeContext.Instance.FindScript(queueTask.ScriptInstanceId);
            }
            totalTasks += script is null ? 1 : script.Users.Count(user => user.Enabled);
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
        catch (Exception ex)
        {
            Logger.Warn($"取消信号发送失败（{exec.TargetName}），任务可能仍在运行：{ex.Message}");
        }
    }

    /// <summary>脚本是否已有进程在运行（按运行时启动目标进程名检测：Args 首项为显式路径时按它，否则主程序）。</summary>
    public static bool IsScriptRunning(ScriptInstance? script)
    {
        if (script is null || string.IsNullOrWhiteSpace(script.MainExe))
        {
            return false;
        }
        string workingDir = string.IsNullOrWhiteSpace(script.RootPath)
            ? Path.GetDirectoryName(script.MainExe) ?? ""
            : script.RootPath;
        (string launchExe, _) = SystemActions.ResolveLaunchTarget(script.MainExe, workingDir, script.Args);
        return SystemActions.IsExeRunning(launchExe);
    }

    /// <summary>队列是否被正在运行的脚本阻塞：返回第一个运行中的脚本名；无则 null。</summary>
    public static string? QueueBlockedBy(DispatchQueue queue)
    {
        foreach (QueueTask task in queue.Tasks.OrderBy(task => task.Index))
        {
            ScriptInstance? script = RuntimeContext.Instance.FindScript(task.ScriptInstanceId);
            if (IsScriptRunning(script))
            {
                return script!.Name;
            }
        }
        return null;
    }

    private static string ExecKindText(RunningExecution exec)
    {
        return exec.Kind == "queue" ? "调度队列" : "脚本实例";
    }

    private void Register(RunningExecution exec, string source)
    {
        if (!_state.TryRegister(exec, out string? error))
        {
            throw new InvalidOperationException(error);
        }
        Audit.Log(source, $"执行{ExecKindText(exec)}", $"{exec.TargetName}（模式：{(exec.Mode == "auto" ? "自动" : "手动")}）");
    }

    /// <summary>运行结束出队：从运行中列表移入已结束列表（CLI 轮询可查询结果；超过 100 条移除最旧）。</summary>
    private void Unregister(RunningExecution exec)
    {
        _state.Unregister(exec);
    }

    /// <summary>
    /// 登记待执行的系统操作（60 秒倒计时，Web 界面可取消）。
    /// execute 非空（sleep）→ 延迟 60 秒后台执行（取消静默跳过，执行后清状态）；
    /// execute 为空（reboot/shutdown，Windows 倒计时权威机制）→ 仅登记 pending，60 秒后清理状态（取消靠 shutdown /a）。
    /// 倒计时 60 秒为用户可见的真实墙钟，不随 NEXUS_TIME_SCALE 缩放（加速档下保持可观测、可断言）。
    /// </summary>
    private void StartPendingSystemAction(string action, string queueName, Action? execute)
    {
        var pending = new PendingSystemAction
        {
            Action = action,
            QueueName = queueName,
            Deadline = DateTime.Now.AddSeconds(60),
        };
        PendingSystemAction? previous = _state.ReplacePending(pending);
        if (previous is not null)
        {
            // v0.7.4：新操作登记前先取消旧 pending 的后台任务——单槽位覆盖时旧 sleep 的
            // Task.Delay 若继续运行，到期仍会执行休眠（60 秒窗口内多个队列先后完成的双系统操作真实触发）。
            try
            {
                previous.Cts.Cancel();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 取消旧系统操作后台任务失败：{ex.Message}");
            }
        }
        if (execute is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(60000, pending.Cts.Token).ConfigureAwait(false);
                    execute();
                }
                catch (OperationCanceledException)
                {
                    // 已取消，不执行
                }
                finally
                {
                    ClearPendingSystemAction(pending);
                }
            });
        }
        else
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(60000).ConfigureAwait(false);
                }
                catch
                {
                }
                finally
                {
                    ClearPendingSystemAction(pending);
                }
            });
        }
    }

    /// <summary>清除待执行系统操作（引用相同才清，避免误清新登记的操作）。</summary>
    private void ClearPendingSystemAction(PendingSystemAction pending)
    {
        _state.ClearPending(pending);
    }

    private async Task RunScriptAsync(RunningExecution exec, ScriptInstance script, string? userName = null)
    {
        try
        {
            List<string?> runUsers;
            if (!string.IsNullOrWhiteSpace(userName))
            {
                runUsers = new List<string?> { userName };
            }
            else
            {
                // v0.7.2+（KN-04）：锁内快照启用用户名单（运行线程与 Web 用户编辑并发时避免枚举冲突）。
                // v0.7.4（KN-22）：StartScript 门禁已保证至少一个启用用户，无用户兜底（Add(null)）不可达，已删除。
                List<string> enabledNames;
                lock (RuntimeContext.Instance.DataLock)
                {
                    enabledNames = script.Users.Where(user => user.Enabled).Select(user => user.Name).ToList();
                }
                runUsers = enabledNames.Cast<string?>().ToList();
            }
            List<RunRecord> records = await RunUsersAsync(exec, script, "", "", runUsers).ConfigureAwait(false);
            if (records.Count > 0 && records[^1].Status == "cancelled")
            {
                exec.Status = "cancelled";
            }
            else if (exec.Status != "cancelled")
            {
                exec.Status = "done";
            }
            if (script.NotifyEnabled)
            {
                foreach (RunRecord record in records)
                {
                    await _notifications.NotifyScriptAsync(script, record).ConfigureAwait(false);
                }
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

    /// <summary>按用户列表依次运行脚本：门禁等待 + 运行会话 + 历史落盘 + 进度更新；取消时中断并标记 cancelled。</summary>
    private async Task<List<RunRecord>> RunUsersAsync(RunningExecution exec, ScriptInstance script, string queueId, string queueName, List<string?> users)
    {
        var records = new List<RunRecord>();
        foreach (string? runUser in users)
        {
            if (exec.Cts.IsCancellationRequested)
            {
                exec.Status = "cancelled";
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
                break;
            }
            try
            {
                var session = new ExecutionCoordinator(
                    script, exec.Mode, queueId, queueName, runUser,
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
                exec.AddRecord(record);
                exec.DoneTasks++;
                exec.CurrentStatus = record.Status == "success" ? "运行成功" : (record.Status == "cancelled" ? "已取消" : "运行失败");
                Logger.Info($"[{(exec.Mode == "auto" ? "自动" : "手动")}运行] 脚本「{displayName}」最终结果：{record.Status}（{record.ResultDetail}）");
                RuntimeContext.Instance.History.Save(record, session.AttemptLogs);
            }
            finally
            {
                gate.Release();
            }
        }
        return records;
    }

    private async Task RunQueueAsync(RunningExecution exec, DispatchQueue queue)
    {
        // v0.6.9+（P7）：exit 完成操作延迟到 finally 收尾（FinishedAt/Unregister）后执行——此前
        // Application.Exit() 立即终止消息循环，队列收尾来不及执行（CLI 轮询查不到结果/状态未出队）。
        bool exitAfterQueue = false;
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
                    exec.AddRecord(missing);
                    exec.DoneTasks++;
                    RuntimeContext.Instance.History.Save(missing, new List<string>());
                    Logger.Warn($"[警告] 调度队列「{queue.Name}」第 {i + 1} 项引用的脚本实例不存在，已跳过。");
                    continue;
                }

                exec.CurrentScriptName = script.Name;
                exec.CurrentAttempt = 0;
                exec.CurrentStatus = "等待开始";

                // v0.7.2+（KN-04）：锁内快照启用用户名单（运行线程与 Web 用户编辑并发时避免枚举冲突）。
                List<string> enabledNames;
                lock (RuntimeContext.Instance.DataLock)
                {
                    enabledNames = script.Users.Where(user => user.Enabled).Select(user => user.Name).ToList();
                }
                List<string?> runUsers = enabledNames.Cast<string?>().ToList();
                if (runUsers.Count == 0)
                {
                    var skipped = new RunRecord
                    {
                        ScriptInstanceId = script.Id,
                        ScriptName = script.Name,
                        QueueId = queue.Id,
                        QueueName = queue.Name,
                        Mode = exec.Mode,
                        StartTime = DateTime.Now,
                        EndTime = DateTime.Now,
                        Status = "failed",
                        ResultDetail = "脚本实例未配置启用用户，已跳过",
                    };
                    records.Add(skipped);
                    exec.AddRecord(skipped);
                    exec.DoneTasks++;
                    RuntimeContext.Instance.History.Save(skipped, new List<string>());
                    Logger.Warn($"[警告] 调度队列「{queue.Name}」第 {i + 1} 项引用的脚本实例「{script.Name}」未配置启用用户，已跳过。");
                    continue;
                }
                records.AddRange(await RunUsersAsync(exec, script, queue.Id, queue.Name, runUsers).ConfigureAwait(false));
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
                await _notifications.NotifyQueueAsync(queue, records).ConfigureAwait(false);
            }
            else
            {
                // v0.7.4（KN-38）：else 分支已隐含 NotifyEnabled=false，原 else if (!queue.NotifyEnabled) 冗余。
                foreach (RunRecord record in records)
                {
                    ScriptInstance? script = RuntimeContext.Instance.FindScript(record.ScriptInstanceId);
                    if (script is not null && script.NotifyEnabled)
                    {
                        await _notifications.NotifyScriptAsync(script, record).ConfigureAwait(false);
                    }
                }
            }

            if (!anyCancelled)
            {
                Logger.Info($"调度队列「{queue.Name}」全部任务执行完毕，执行完成操作：{QueueRule.CompletionActionDesc(queue.CompletionAction)}。");
                switch (queue.CompletionAction)
                {
                    case "exit":
                        // 退出软件保持立即执行语义（无倒计时、不可取消），但延迟到队列收尾（finally）后真正退出。
                        exitAfterQueue = true;
                        break;
                    case "sleep":
                        // 休眠走应用内延迟：60 秒后执行 Hibernate()，期间 Web 界面可取消（Cts.Cancel 后不执行）。
                        // 倒计时 60 秒为用户可见的真实墙钟，不随 NEXUS_TIME_SCALE 缩放（加速档下保持可观测、可断言）。
                        StartPendingSystemAction("sleep", queue.Name, SystemActions.Hibernate);
                        break;
                    case "reboot":
                        // 重启走 Windows 倒计时（shutdown /r /t 60）：即使宿主崩溃仍会执行，取消靠 shutdown /a；pending 状态供 UI 展示取消路径。
                        StartPendingSystemAction("reboot", queue.Name, null);
                        SystemActions.Reboot(60);
                        break;
                    case "shutdown":
                        StartPendingSystemAction("shutdown", queue.Name, null);
                        SystemActions.Shutdown(60);
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
            if (exitAfterQueue)
            {
                SystemActions.ExitApp();
            }
        }
    }
}
