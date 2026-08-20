namespace NexusPipeline.Services;

/// <summary>
/// 运行状态存储：集中管理运行中/已结束任务和待执行系统操作的并发访问。
/// 业务编排不再直接持有这些集合，避免状态生命周期与执行流程互相缠绕。
/// </summary>
internal sealed class ExecutionStateStore
{
    private readonly List<RunningExecution> _active = new();

    private readonly List<RunningExecution> _finished = new();

    private PendingSystemAction? _pendingSystemAction;

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

    public RunningExecution? FindAny(string id)
    {
        lock (_sync)
        {
            return _active.FirstOrDefault(exec => exec.Id == id)
                ?? _finished.FirstOrDefault(exec => exec.Id == id);
        }
    }

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

    /// <summary>按原有注册顺序执行原子防重入检查并登记任务。</summary>
    public bool TryRegister(RunningExecution exec, out string? error)
    {
        lock (_sync)
        {
            if (exec.Kind == "script" && _active.Any(active => active.Kind == "script" && active.TargetId == exec.TargetId))
            {
                error = $"脚本「{exec.TargetName}」正在运行，请先退出后再执行";
                return false;
            }
            if (exec.Kind == "queue" && _active.Any(active => active.Kind == "queue" && active.TargetId == exec.TargetId))
            {
                error = $"调度队列「{exec.TargetName}」正在运行，请先完成后再执行";
                return false;
            }
            if (exec.Kind == "queue" && _active.Any(active => active.Kind == "queue"))
            {
                error = $"已有其他调度队列正在运行，当前队列「{exec.TargetName}」暂不能并行执行";
                return false;
            }
            _active.Add(exec);
            error = null;
            return true;
        }
    }

    public void Unregister(RunningExecution exec)
    {
        lock (_sync)
        {
            _active.Remove(exec);
            _finished.Add(exec);
            if (_finished.Count > 100)
            {
                _finished.RemoveRange(0, _finished.Count - 100);
            }
        }
    }

    public PendingSystemAction? ReplacePending(PendingSystemAction pending)
    {
        lock (_sync)
        {
            PendingSystemAction? previous = _pendingSystemAction;
            _pendingSystemAction = pending;
            return previous;
        }
    }

    public bool TryTakePending(out PendingSystemAction? pending)
    {
        lock (_sync)
        {
            pending = _pendingSystemAction;
            if (pending is null)
            {
                return false;
            }
            _pendingSystemAction = null;
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
            }
        }
    }
}
