using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Execution;

/// <summary>系统完成操作的延迟登记、取消和单槽位替换策略。</summary>
internal sealed class SystemActionExecutor
{
    private readonly ExecutionStateStore _state;

    public SystemActionExecutor(ExecutionStateStore state)
    {
        _state = state;
    }

    public PendingSystemAction? Current => _state.CurrentSystemAction;

    /// <summary>取消待执行系统操作：sleep 取消应用内延迟；reboot/shutdown 调用 shutdown /a。</summary>
    public bool Cancel(string source = Audit.Web)
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
        Audit.Log(source, "取消系统操作", $"{action}（{queueName}）");
        return true;
    }

    /// <summary>
    /// 登记待执行系统操作。倒计时保持真实 60 秒，不受 NEXUS_TIME_SCALE 缩放，
    /// 以保留现有 Web 取消和验收断言语义。
    /// </summary>
    public void Schedule(string action, string queueName, Action? execute)
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
                    // 已取消，不执行。
                }
                finally
                {
                    _state.ClearPending(pending);
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
                    _state.ClearPending(pending);
                }
            });
        }
    }
}
