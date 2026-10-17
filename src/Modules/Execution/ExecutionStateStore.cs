using NexusPipeline.Modules.Configuration.Contracts;
using NexusPipeline.Modules.Configuration.Recovery;

namespace NexusPipeline.Modules.Execution;

/// <summary>
/// 运行状态存储：集中管理运行中/已结束任务、准入 profile、完成意图和待执行系统操作。
/// 准入检查、资源租约登记与活动运行登记均在同一把锁内完成。
/// </summary>
internal sealed partial class ExecutionStateStore
{
    private ExecutionGroupState _groupState = ExecutionGroupState.Open;

    private bool _maintenanceActive;

    private readonly List<RunningExecution> _active = new();

    private readonly List<RunningExecution> _finished = new();

    private readonly Dictionary<string, ExecutionAdmissionProfile> _admissions = new();

    private readonly Dictionary<string, ExecutionResourceSet> _editSessionLeases = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<CompletionIntent> _completionIntents = new();

    private readonly ExecutionAdmissionPolicy _policy;
    private readonly Func<IReadOnlyList<ConfigRecoveryIsolationProjection>> _providerRecovery;

    private PendingSystemAction? _pendingSystemAction;

    private readonly object _coordinationSync = new();

    private readonly object _sync = new();

    public ExecutionStateStore(ExecutionAdmissionPolicy? policy = null, Func<IReadOnlyList<ConfigRecoveryIsolationProjection>>? providerRecovery = null)
    {
        _policy = policy ?? new ExecutionAdmissionPolicy();
        _providerRecovery = providerRecovery ?? (() => []);
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

    /// <summary>返回诊断所需的协调状态快照；不暴露可变运行对象或准入 profile。</summary>
    internal ExecutionStateSnapshot SnapshotState()
    {
        lock (_coordinationSync)
        {
            lock (_sync)
            {
                return new ExecutionStateSnapshot(
                    _groupState,
                    _maintenanceActive,
                    _active.Count,
                    _finished.Count,
                    _editSessionLeases.Count,
                    _completionIntents.Count,
                    _pendingSystemAction is not null);
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

    /// <summary>
    /// 只读评估候选计划。它与 TryRegister 共享同一协调域和同一准入策略，
    /// 因此 dry-run 与真实启动看到的是同一组活动租约和门禁状态。
    /// </summary>
    public ExecutionAdmissionFailure? EvaluateCandidate(
        string candidateKind,
        string candidateTargetId,
        string candidateTargetName,
        ExecutionAdmissionProfile profile)
    {
        lock (_coordinationSync)
        {
            lock (_sync)
            {
                return EvaluateCandidateLocked(candidateKind, candidateTargetId, candidateTargetName, profile);
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
                failure = EvaluateCandidateLocked(exec.Kind, exec.TargetId, exec.TargetName, profile);
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

    private ExecutionAdmissionFailure? EvaluateCandidateLocked(
        string candidateKind,
        string candidateTargetId,
        string candidateTargetName,
        ExecutionAdmissionProfile profile)
    {
        if (_maintenanceActive || _groupState == ExecutionGroupState.Maintenance)
        {
            return new ExecutionAdmissionFailure(
                ExecutionAdmissionFailureCode.HostMaintenance,
                "宿主正在进行维护操作，暂不能启动新任务");
        }
        if (_pendingSystemAction is not null)
        {
            return new ExecutionAdmissionFailure(
                ExecutionAdmissionFailureCode.PendingSystemAction,
                "系统完成操作正在等待执行，请先取消后再启动新任务");
        }

        if (_groupState == ExecutionGroupState.Closing)
        {
            return new ExecutionAdmissionFailure(
                ExecutionAdmissionFailureCode.ExecutionGroupClosing,
                "当前并行运行组已进入收尾阶段，新的任务暂不能加入");
        }

        // A queue is admitted as an ordered scan; each frozen item is checked again
        // immediately before its own start. Unknown scope still blocks the whole scan.
        string? isolationConflict = FindRecoveryIsolationConflictLocked(profile.Resources,
            unknownOnly: candidateKind == "queue");
        if (isolationConflict is not null)
            return new ExecutionAdmissionFailure(ExecutionAdmissionFailureCode.ResourceConflict,
                $"存在未解决的恢复隔离（{isolationConflict}）", Resource: isolationConflict);

        foreach ((string leaseKey, ExecutionResourceSet leaseResources) in _editSessionLeases)
        {
            string? editConflict = profile.Resources.FindConflict(leaseResources);
            if (editConflict is not null)
            {
                return new ExecutionAdmissionFailure(
                    ExecutionAdmissionFailureCode.ResourceConflict,
                    $"当前执行与配置编辑会话「{leaseKey}」存在资源冲突（{editConflict}）",
                    Resource: editConflict);
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
        return _policy.Evaluate(
            candidateKind,
            candidateTargetId,
            candidateTargetName,
            profile,
            active,
            _completionIntents);
    }

    internal string? FindRecoveryIsolationConflict(ExecutionResourceSet candidate)
    {
        lock (_coordinationSync)
        {
            lock (_sync) return FindRecoveryIsolationConflictLocked(candidate, unknownOnly: false);
        }
    }

    private IReadOnlyList<ConfigRecoveryIsolationProjection> UnownedRecoveryIsolationLocked() =>
        ConfigRecoveryService.SnapshotIsolation()
            .Concat(_providerRecovery())
            .Concat(ProviderWorkerArtifacts.SnapshotIsolation())
            .Where(isolation => isolation.ExplicitIsolation
                || string.IsNullOrWhiteSpace(isolation.OriginExecutionId)
                || !_active.Any(exec => exec.Id == isolation.OriginExecutionId))
            .ToArray();

    private string? FindRecoveryIsolationConflictLocked(ExecutionResourceSet candidate, bool unknownOnly)
    {
        foreach (ConfigRecoveryIsolationProjection isolation in UnownedRecoveryIsolationLocked())
        {
            if (isolation.ScopeQuality != "complete" || !isolation.MayContinueIndependent)
                return $"recovery:{isolation.CauseCode}";
            if (unknownOnly) continue;
            ExecutionResourceSet isolated = ExecutionResourceSet.Empty with
            {
                ScriptIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { $"script:{isolation.ScriptId}" },
                ConfigPaths = new[] { isolation.ConfigPath }.Concat(isolation.ExtraConfigPaths)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(ExecutionResourceSetBuilder.NormalizePath).ToArray(),
                WritableRoots = string.IsNullOrWhiteSpace(isolation.WritableRoot)
                    ? Array.Empty<string>() : new[] { ExecutionResourceSetBuilder.NormalizePath(isolation.WritableRoot) },
            };
            string? conflict = candidate.FindConflict(isolated);
            if (conflict is not null) return conflict;
        }
        return null;
    }
}
