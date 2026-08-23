using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Execution;

/// <summary>
/// 并行运行组完成操作协调器：完成意图先登记，所有活动运行退出后才原子预留并执行系统操作。
/// </summary>
internal sealed class SystemActionExecutor
{
    private readonly ExecutionStateStore _state;

    public SystemActionExecutor(ExecutionStateStore state)
    {
        _state = state;
    }

    public PendingSystemAction? Current => _state.CurrentSystemAction;

    /// <summary>运行释放入口；只有最后一个活动运行释放时才会得到 idle pending action。</summary>
    public void CompleteExecution(RunningExecution exec, CompletionIntent? intent)
    {
        PendingSystemAction? pending = _state.Release(exec, intent);
        if (pending is not null)
        {
            Arm(pending);
        }
    }

    /// <summary>取消待执行系统操作：sleep/reboot/shutdown 可取消，exit 保持立即退出语义。</summary>
    public bool Cancel(string source = Audit.Web)
    {
        if (!_state.TryBeginCancelPending(out PendingSystemAction? pending) || pending is null)
        {
            return false;
        }

        string action = pending.Action;
        string queueName = pending.QueueName;
        bool osCancelSucceeded = false;
        try
        {
            pending.Cts.Cancel();
            if (action is "reboot" or "shutdown")
            {
                if (!SystemActions.CancelShutdown())
                {
                    throw new InvalidOperationException("OS 取消关机/重启命令返回失败");
                }
            }
            osCancelSucceeded = true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 取消系统操作「{action}」失败：{ex.Message}");
        }
        bool cleared = _state.CompleteCancelPending(pending, osCancelSucceeded);
        Audit.Log(source, cleared ? "取消系统操作" : "取消系统操作失败", $"{action}（{queueName}）");
        return cleared;
    }

    /// <summary>
    /// 兼容旧调用方的直接调度入口。并行队列完成路径不再使用 replacement 语义，而是经 CompleteExecution。
    /// 倒计时保持真实 60 秒，不受 NEXUS_TIME_SCALE 缩放。
    /// </summary>
    public void Schedule(string action, string queueName, Action? execute)
    {
        var pending = new PendingSystemAction
        {
            Action = ExecutionAdmissionProfile.NormalizeCompletionAction(action),
            QueueName = queueName,
            Deadline = DateTime.Now.AddSeconds(60),
        };
        PendingSystemAction? previous = _state.ReplacePending(pending);
        if (previous is not null)
        {
            try
            {
                previous.Cts.Cancel();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 取消旧系统操作后台任务失败：{ex.Message}");
            }
        }
        StartDelay(pending, execute, pending.Action == "exit" ? TimeSpan.Zero : TimeSpan.FromSeconds(60));
    }

    private void Arm(PendingSystemAction pending)
    {
        try
        {
            switch (pending.Action)
            {
                case "reboot":
                    if (_state.TryArm(pending))
                    {
                        SystemActions.Reboot(60);
                        StartDelay(pending, null, TimeSpan.FromSeconds(60));
                    }
                    break;
                case "shutdown":
                    if (_state.TryArm(pending))
                    {
                        SystemActions.Shutdown(60);
                        StartDelay(pending, null, TimeSpan.FromSeconds(60));
                    }
                    break;
                case "sleep":
                    StartDelay(pending, SystemActions.Hibernate, TimeSpan.FromSeconds(60));
                    break;
                case "exit":
                    StartDelay(pending, SystemActions.ExitApp, TimeSpan.Zero);
                    break;
                default:
                    _state.ClearPending(pending);
                    Logger.Warn($"[警告] 未识别的完成操作「{pending.Action}」，已跳过。");
                    break;
            }
        }
        catch (Exception ex)
        {
            _state.ClearPending(pending);
            Logger.Warn($"[警告] 启动完成操作「{pending.Action}」失败：{ex.Message}");
        }
    }

    private void StartDelay(PendingSystemAction pending, Action? execute, TimeSpan delay)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, pending.Cts.Token).ConfigureAwait(false);
                if (!pending.Cts.IsCancellationRequested)
                {
                    execute?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
                // 已取消，不执行。
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 完成操作「{pending.Action}」执行失败：{ex.Message}");
            }
            finally
            {
                _state.ClearPending(pending);
            }
        });
    }
}
