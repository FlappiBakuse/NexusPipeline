namespace NexusPipeline.Services.Execution;

/// <summary>
/// 运行状态存储：集中管理运行中/已结束任务、准入 profile、完成意图和待执行系统操作。
/// 准入检查、资源租约登记与活动运行登记均在同一把锁内完成。
/// </summary>
internal sealed class ExecutionStateStore
{
    private ExecutionGroupState _groupState = ExecutionGroupState.Open;

    private readonly List<RunningExecution> _active = new();

    private readonly List<RunningExecution> _finished = new();

    private readonly Dictionary<string, ExecutionAdmissionProfile> _admissions = new();

    private readonly Dictionary<string, ExecutionResourceSet> _editSessionLeases = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<CompletionIntent> _completionIntents = new();

    private readonly ExecutionAdmissionPolicy _policy;

    private PendingSystemAction? _pendingSystemAction;

    private readonly object _coordinationSync = new();

    private readonly object _sync = new();

    public ExecutionStateStore(ExecutionAdmissionPolicy? policy = null)
    {
        _policy = policy ?? new ExecutionAdmissionPolicy();
    }

    internal ExecutionGroupState GroupState
    {
        get
        {
            lock (_sync)
            {
                return _groupState;
            }
        }
    }

    /// <summary>
    /// 将计划建立、准入登记与数据生命周期门禁置于同一协调域。
    /// 计划构建本身仍不持有状态锁，避免文件/仓储读取进入状态临界区。
    /// </summary>
    public T WithAdmissionCoordination<T>(Func<T> action)
    {
        lock (_coordinationSync)
        {
            return action();
        }
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
        lock (_coordinationSync)
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

                if (_groupState == ExecutionGroupState.Closing)
                {
                    failure = new ExecutionAdmissionFailure(
                        ExecutionAdmissionFailureCode.ExecutionGroupClosing,
                        "当前并行运行组已进入收尾阶段，新的任务暂不能加入");
                    return false;
                }

                foreach ((string leaseKey, ExecutionResourceSet leaseResources) in _editSessionLeases)
                {
                    string? editConflict = profile.Resources.FindConflict(leaseResources);
                    if (editConflict is not null)
                    {
                        failure = new ExecutionAdmissionFailure(
                            ExecutionAdmissionFailureCode.ResourceConflict,
                            $"当前执行与配置编辑会话「{leaseKey}」存在资源冲突（{editConflict}）",
                            Resource: editConflict);
                        return false;
                    }
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

    /// <summary>兼容旧内部调用方：取消动作已由新状态机完成后再提供原子成功语义。</summary>
    public bool TryCancelPending(out PendingSystemAction? pending)
    {
        if (!TryBeginCancelPending(out pending) || pending is null)
        {
            return false;
        }
        return CompleteCancelPending(pending, osCancelSucceeded: true);
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

    /// <summary>兼容旧测试/调用方；状态转换仍在锁内，回调在锁外执行。</summary>
    public bool TryArm(PendingSystemAction pending, Action arm)
    {
        if (!TryArm(pending))
        {
            return false;
        }
        arm();
        return true;
    }

    // 以下三个方法保留给旧测试与兼容调用方；新完成操作统一经 Release 的 idle arm 语义。
    public PendingSystemAction? ReplacePending(PendingSystemAction pending)
    {
        lock (_sync)
        {
            PendingSystemAction? previous = _pendingSystemAction;
            _pendingSystemAction = pending;
            _completionIntents.Clear();
            _groupState = ExecutionGroupState.ActionPending;
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
            _groupState = ExecutionGroupState.Open;
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

    /// <summary>查询活动执行对脚本或用户数据的引用，供 Web/CLI 破坏性 CRUD 使用同一租约来源。</summary>
    public IReadOnlyList<ExecutionLeaseReference> FindLeases(string scriptId, string? userName = null)
    {
        lock (_sync)
        {
            return FindLeasesLocked(scriptId, userName);
        }
    }

    /// <summary>在准入协调锁内检查租约并执行同步数据变更，消除“检查后到删除前”的竞态窗口。</summary>
    public bool TryExecuteLeaseMutation(
        string scriptId,
        string? userName,
        Action mutation,
        out IReadOnlyList<ExecutionLeaseReference> leases)
    {
        lock (_coordinationSync)
        {
            leases = FindLeases(scriptId, userName);
            if (leases.Count > 0)
            {
                return false;
            }
            mutation();
            return true;
        }
    }

    /// <summary>把队列 CRUD 也纳入准入协调域，避免检查完成后执行计划才登记的 TOCTOU 窗口。</summary>
    public bool TryExecuteQueueLeaseMutation(
        string queueId,
        Action mutation,
        out IReadOnlyList<ExecutionLeaseReference> leases)
    {
        lock (_coordinationSync)
        {
            lock (_sync)
            {
                leases = _active
                    .Where(exec => exec.Kind == "queue"
                        && string.Equals(exec.TargetId, queueId, StringComparison.Ordinal))
                    .Select(exec => new ExecutionLeaseReference(
                        exec.Id,
                        exec.Kind,
                        exec.TargetId,
                        exec.TargetName))
                    .ToList();
                if (leases.Count > 0)
                {
                    return false;
                }
                mutation();
                return true;
            }
        }
    }

    public bool TryExecuteAnyQueueLeaseMutation(
        Action mutation,
        out IReadOnlyList<ExecutionLeaseReference> leases)
    {
        lock (_coordinationSync)
        {
            lock (_sync)
            {
                leases = _active
                    .Where(exec => exec.Kind == "queue")
                    .Select(exec => new ExecutionLeaseReference(
                        exec.Id,
                        exec.Kind,
                        exec.TargetId,
                        exec.TargetName))
                    .ToList();
                if (leases.Count > 0)
                {
                    return false;
                }
                mutation();
                return true;
            }
        }
    }

    /// <summary>以脚本、用户和配置文件资源建立编辑会话租约。</summary>
    public bool TryBeginEditSession(
        string scriptId,
        string userName,
        string configPath,
        out string? conflict)
    {
        string normalizedScriptId = scriptId.Trim();
        string normalizedUser = userName.Trim();
        string leaseKey = $"{normalizedScriptId}:{normalizedUser}";
        ExecutionResourceSet resources = ExecutionResourceSetBuilder.Build(
                new[] { new ExecutionResourceInput(normalizedScriptId, null, new[] { normalizedUser }) })
            with
            {
                ConfigPaths = string.IsNullOrWhiteSpace(configPath)
                    ? Array.Empty<string>()
                    : new[] { Path.GetFullPath(configPath) },
            };

        lock (_coordinationSync)
        {
            lock (_sync)
            {
                foreach (ExecutionAdmissionEntry active in _active.Select(exec => new ExecutionAdmissionEntry(
                    exec.Id,
                    exec.Kind,
                    exec.TargetId,
                    exec.TargetName,
                    _admissions[exec.Id])))
                {
                    string? resource = resources.FindConflict(active.Profile.Resources);
                    if (resource is not null)
                    {
                        conflict = $"运行「{active.TargetName}」已占用资源 {resource}";
                        return false;
                    }
                }

                foreach ((string existingKey, ExecutionResourceSet existing) in _editSessionLeases)
                {
                    string? resource = resources.FindConflict(existing);
                    if (resource is not null)
                    {
                        conflict = $"配置编辑会话「{existingKey}」已占用资源 {resource}";
                        return false;
                    }
                }

                _editSessionLeases[leaseKey] = resources;
                conflict = null;
                return true;
            }
        }
    }

    public void EndEditSession(string scriptId, string userName)
    {
        string leaseKey = $"{scriptId.Trim()}:{userName.Trim()}";
        lock (_coordinationSync)
        {
            lock (_sync)
            {
                _editSessionLeases.Remove(leaseKey);
            }
        }
    }

    private IReadOnlyList<ExecutionLeaseReference> FindLeasesLocked(string scriptId, string? userName)
    {
        string scriptKey = $"script:{scriptId.Trim()}";
        string? userKey = string.IsNullOrWhiteSpace(userName)
            ? null
            : $"user:{scriptId.Trim()}:{userName.Trim()}";
        return _active
            .Select(exec => new
            {
                Exec = exec,
                Profile = _admissions[exec.Id],
            })
            .Where(item => item.Profile.Resources.ScriptIds.Contains(scriptKey)
                && (userKey is null || item.Profile.Resources.UserDataKeys.Contains(userKey)))
            .Select(item => new ExecutionLeaseReference(
                item.Exec.Id,
                item.Exec.Kind,
                item.Exec.TargetId,
                item.Exec.TargetName))
            .ToList();
    }
}

internal enum ExecutionGroupState
{
    Open,
    Closing,
    ActionPending,
    Cancelling,
}

internal sealed record ExecutionLeaseReference(
    string RunId,
    string Kind,
    string TargetId,
    string TargetName);
