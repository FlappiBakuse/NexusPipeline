namespace NexusPipeline.Services.Execution;

/// <summary>
/// 运行状态存储：集中管理运行中/已结束任务、准入 profile、完成意图和待执行系统操作。
/// 准入检查、资源租约登记与活动运行登记均在同一把锁内完成。
/// </summary>
internal sealed class ExecutionStateStore
{
    private readonly List<RunningExecution> _active = new();

    private readonly List<RunningExecution> _finished = new();

    private readonly Dictionary<string, ExecutionAdmissionProfile> _admissions = new();

    private readonly List<CompletionIntent> _completionIntents = new();

    private readonly ExecutionAdmissionPolicy _policy;

    private PendingSystemAction? _pendingSystemAction;

    private readonly object _sync = new();

    public ExecutionStateStore(ExecutionAdmissionPolicy? policy = null)
    {
        _policy = policy ?? new ExecutionAdmissionPolicy();
    }

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

    internal IReadOnlyList<ExecutionAdmissionEntry> ActiveAdmissions
    {
        get
        {
            lock (_sync)
            {
                return _active
                    .Select(exec => new ExecutionAdmissionEntry(
                        exec.Id,
                        exec.Kind,
                        exec.TargetId,
                        exec.TargetName,
                        _admissions[exec.Id]))
                    .ToList();
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

    /// <summary>
    /// 在同一临界区执行“系统操作状态检查 + 资格矩阵 + 资源冲突 + 完成操作兼容性 + 登记”。
    /// </summary>
    public bool TryRegister(
        RunningExecution exec,
        ExecutionAdmissionProfile profile,
        out ExecutionAdmissionFailure? failure)
    {
        lock (_sync)
        {
            if (_pendingSystemAction is not null)
            {
                failure = new ExecutionAdmissionFailure(
                    ExecutionAdmissionFailureCode.PendingSystemAction,
                    "系统完成操作正在等待执行，请先取消后再启动新任务");
                return false;
            }

            List<ExecutionAdmissionEntry> active = _active
                .Select(item => new ExecutionAdmissionEntry(
                    item.Id,
                    item.Kind,
                    item.TargetId,
                    item.TargetName,
                    _admissions[item.Id]))
                .ToList();
            failure = _policy.Evaluate(
                exec.Kind,
                exec.TargetId,
                exec.TargetName,
                profile,
                active,
                _completionIntents);
            if (failure is not null)
            {
                return false;
            }

            _active.Add(exec);
            _admissions[exec.Id] = profile;
            return true;
        }
    }

    /// <summary>兼容旧内部调用方的入口；新执行链必须传入冻结的 profile。</summary>
    public bool TryRegister(RunningExecution exec, out string? error)
    {
        bool accepted = TryRegister(exec, ExecutionAdmissionProfile.Legacy(exec), out ExecutionAdmissionFailure? failure);
        error = failure?.Message;
        return accepted;
    }

    /// <summary>
    /// 释放活动运行并提交完成意图；若这次释放使系统空闲且存在意图，则在同一锁内预留 pending action。
    /// 返回值交给 SystemActionExecutor 启动倒计时或系统操作。
    /// </summary>
    public PendingSystemAction? Release(RunningExecution exec, CompletionIntent? intent)
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

            if (intent is not null)
            {
                string action = ExecutionAdmissionProfile.NormalizeCompletionAction(intent.Action);
                if (action != "none")
                {
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
            return _pendingSystemAction;
        }
    }

    public void Unregister(RunningExecution exec)
    {
        Release(exec, null);
    }

    /// <summary>取消可取消的 pending 系统操作，并清除尚未执行的完成意图。</summary>
    public bool TryCancelPending(out PendingSystemAction? pending)
    {
        lock (_sync)
        {
            pending = _pendingSystemAction;
            if (pending is null || pending.Action == "exit")
            {
                pending = null;
                return false;
            }
            _pendingSystemAction = null;
            _completionIntents.Clear();
            return true;
        }
    }

    /// <summary>
    /// 在状态锁内确认 pending 仍属于当前完成操作后执行一次需要立即发出的系统命令。
    /// 解决完成线程尚未开始 Arm、用户已取消 pending 时的竞态。
    /// </summary>
    public bool TryArm(PendingSystemAction pending, Action arm)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_pendingSystemAction, pending) || pending.Cts.IsCancellationRequested)
            {
                return false;
            }
            arm();
            return true;
        }
    }

    // 以下三个方法保留给旧测试与兼容调用方；新完成操作统一经 Release 的 idle arm 语义。
    public PendingSystemAction? ReplacePending(PendingSystemAction pending)
    {
        lock (_sync)
        {
            PendingSystemAction? previous = _pendingSystemAction;
            _pendingSystemAction = pending;
            _completionIntents.Clear();
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
            _completionIntents.Clear();
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
            }
        }
    }
}
