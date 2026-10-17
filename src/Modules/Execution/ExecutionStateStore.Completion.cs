using NexusPipeline.Modules.Configuration.Contracts;
using NexusPipeline.Modules.Configuration.Recovery;

namespace NexusPipeline.Modules.Execution;

internal sealed partial class ExecutionStateStore
{
    public PendingSystemAction? CurrentSystemAction
    {
        get
        {
            lock (_sync)
            {
                return _pendingSystemAction is null
                    ? null
                    : new PendingSystemAction
                    {
                        Action = _pendingSystemAction.Action,
                        QueueName = _pendingSystemAction.QueueName,
                        Deadline = _pendingSystemAction.Deadline,
                    };
            }
        }
    }

    /// <summary>
    /// 释放活动运行并提交完成意图；若这次释放使系统空闲且存在意图，则在同一锁内预留 pending action。
    /// 返回值交给 SystemActionExecutor 启动倒计时或系统操作。
    /// </summary>
    public PendingSystemAction? Release(RunningExecution exec, CompletionIntent? intent)
    {
        lock (_coordinationSync)
        {
            lock (_sync)
            {
                if (!_active.Remove(exec))
                {
                    return null;
                }

                _admissions.Remove(exec.Id);
                _finished.Add(exec);
                if (_finished.Count > 100)
                {
                    _finished.RemoveRange(0, _finished.Count - 100);
                }

                if (UnownedRecoveryIsolationLocked().Count > 0)
                {
                    intent = null;
                    _completionIntents.Clear();
                    if (_groupState == ExecutionGroupState.Closing) _groupState = ExecutionGroupState.Open;
                }
                if (intent is not null)
                {
                    string action = ExecutionAdmissionProfile.NormalizeCompletionAction(intent.Action);
                    if (action != "none")
                    {
                        _groupState = ExecutionGroupState.Closing;
                        _completionIntents.Add(intent with { Action = action });
                    }
                }

                if (_active.Count > 0 || _completionIntents.Count == 0 || _pendingSystemAction is not null)
                {
                    return null;
                }

                string actionToArm = _completionIntents[0].Action;
                string queueName = string.Join(
                    "、",
                    _completionIntents
                        .Select(item => item.QueueName)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.Ordinal)
                        .Take(8));
                _completionIntents.Clear();
                _pendingSystemAction = new PendingSystemAction
                {
                    Action = actionToArm,
                    QueueName = queueName,
                    Deadline = actionToArm == "exit"
                        ? DateTime.Now
                        : DateTime.Now.AddSeconds(60),
                };
                _groupState = ExecutionGroupState.ActionPending;
                return _pendingSystemAction;
            }
        }
    }

    public void Unregister(RunningExecution exec)
    {
        Release(exec, null);
    }

    /// <summary>将可取消的 pending 系统操作转入 Cancelling；OS 副作用在锁外执行。</summary>
    public bool TryBeginCancelPending(out PendingSystemAction? pending)
    {
        lock (_sync)
        {
            pending = _pendingSystemAction;
            if (pending is null || pending.Action == "exit")
            {
                pending = null;
                return false;
            }
            _groupState = ExecutionGroupState.Cancelling;
            return true;
        }
    }

    /// <summary>提交 pending 取消结果；只有 OS 取消成功才释放 pending 与准入门禁。</summary>
    public bool CompleteCancelPending(PendingSystemAction pending, bool osCancelSucceeded)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_pendingSystemAction, pending) || pending.Action == "exit")
            {
                return false;
            }
            if (!osCancelSucceeded)
            {
                _groupState = ExecutionGroupState.Cancelling;
                return false;
            }
            _pendingSystemAction = null;
            _completionIntents.Clear();
            _groupState = ExecutionGroupState.Open;
            return true;
        }
    }

    /// <summary>
    /// 在状态锁内只完成 pending → armed 状态转换；实际系统调用必须在锁外执行。
    /// </summary>
    public bool TryArm(PendingSystemAction pending)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_pendingSystemAction, pending)
                || pending.Cts.IsCancellationRequested
                || pending.IsArmed)
            {
                return false;
            }
            pending.IsArmed = true;
            return true;
        }
    }

    public void ClearPending(PendingSystemAction pending)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_pendingSystemAction, pending))
            {
                _pendingSystemAction = null;
                _completionIntents.Clear();
                _groupState = ExecutionGroupState.Open;
            }
        }
    }
}

internal enum ExecutionGroupState
{
    Open,
    Closing,
    ActionPending,
    Cancelling,
    Maintenance,
}

internal sealed record ExecutionStateSnapshot(
    ExecutionGroupState GroupState,
    bool MaintenanceActive,
    int ActiveCount,
    int FinishedCount,
    int EditSessionCount,
    int CompletionIntentCount,
    bool PendingSystemAction);
