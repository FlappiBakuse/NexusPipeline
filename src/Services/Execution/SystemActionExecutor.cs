using NexusPipeline.Utilities;
using NexusPipeline.Services.Realtime;

namespace NexusPipeline.Services.Execution;

/// <summary>
/// 并行运行组完成操作协调器：完成意图先登记，所有活动运行退出后才原子预留并执行系统操作。
/// </summary>
internal sealed class SystemActionExecutor
{
    private readonly ExecutionStateStore _state;

    private readonly RealtimeEventBus? _realtime;

    public SystemActionExecutor(ExecutionStateStore state, RealtimeEventBus? realtime = null)
    {
        _state = state;
        _realtime = realtime;
    }

    public PendingSystemAction? Current => _state.CurrentSystemAction;

    /// <summary>运行释放入口；只有最后一个活动运行释放时才会得到 idle pending action。</summary>
    public void CompleteExecution(RunningExecution exec, CompletionIntent? intent)
    {
        PendingSystemAction? pending = _state.Release(exec, intent);
        _realtime?.FlushPendingLogs(exec.Id);
        PublishRunFinished(exec);
        if (pending is not null)
        {
            PublishSystemAction("pending", pending);
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
        if (cleared)
        {
            PublishSystemAction("cancelled", action, queueName, null);
            PublishHostStatus();
        }
        Audit.Log(source, cleared ? "取消系统操作" : "取消系统操作失败", $"{action}（{queueName}）");
        return cleared;
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
                        PublishSystemAction("armed", pending);
                        StartDelay(pending, null, TimeSpan.FromSeconds(60));
                    }
                    break;
                case "shutdown":
                    if (_state.TryArm(pending))
                    {
                        SystemActions.Shutdown(60);
                        PublishSystemAction("armed", pending);
                        StartDelay(pending, null, TimeSpan.FromSeconds(60));
                    }
                    break;
                case "sleep":
                    if (_state.TryArm(pending))
                    {
                        PublishSystemAction("armed", pending);
                        StartDelay(pending, SystemActions.Hibernate, TimeSpan.FromSeconds(60));
                    }
                    break;
                case "exit":
                    if (_state.TryArm(pending))
                    {
                        PublishSystemAction("armed", pending);
                        StartDelay(pending, SystemActions.ExitApp, TimeSpan.Zero);
                    }
                    break;
                default:
                    _state.ClearPending(pending);
                    PublishSystemAction("cleared", pending);
                    PublishHostStatus();
                    Logger.Warn($"[警告] 未识别的完成操作「{pending.Action}」，已跳过。");
                    break;
            }
        }
        catch (Exception ex)
        {
            _state.ClearPending(pending);
            PublishSystemAction("cleared", pending);
            PublishHostStatus();
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
                PublishSystemAction("cleared", pending);
                PublishHostStatus();
            }
        });
    }

    private void PublishRunFinished(RunningExecution exec)
    {
        if (_realtime is null)
        {
            return;
        }
        _realtime.Publish(
            RealtimeEventNames.RunStatus,
            RealtimeEventProjection.RunStatus(exec.SnapshotStatus(), active: false));
        PublishHostStatus();
    }

    private void PublishSystemAction(string state, PendingSystemAction pending)
    {
        PublishSystemAction(state, pending.Action, pending.QueueName, pending.Deadline);
    }

    private void PublishSystemAction(
        string state,
        string action,
        string queueName,
        DateTime? deadline)
    {
        _realtime?.Publish(
            RealtimeEventNames.SystemAction,
            RealtimeEventProjection.SystemAction(state, action, queueName, deadline));
    }

    private void PublishHostStatus()
    {
        if (_realtime is null)
        {
            return;
        }
        _realtime.Publish(
            RealtimeEventNames.HostStatus,
            RealtimeEventProjection.HostStatus(
                _state.Active.Select(exec => exec.SnapshotStatus()).ToList()));
    }
}
