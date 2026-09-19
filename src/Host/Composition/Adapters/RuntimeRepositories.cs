using NexusPipeline.Host.State;
using NexusPipeline.Modules.Execution.Contracts;
using NexusPipeline.Modules.Queues.Contracts;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Users.Bindings;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Modules.Users;
using NexusPipeline.Modules.Scheduling;

namespace NexusPipeline.Host.Composition.Adapters;

/// <summary>运行时脚本仓储适配器：读取只经过运行时实体状态端口。</summary>
internal sealed class RuntimeScriptRepository : IScriptRepository
{
    private readonly AutomationDefinitionState _state;

    public RuntimeScriptRepository(AutomationDefinitionState state)
    {
        _state = state;
    }

    public ScriptInstance? FindById(string id) => _state.FindScript(id);

    public IReadOnlyList<ScriptInstance> Snapshot() => _state.SnapshotScripts();
}

/// <summary>运行时队列仓储适配器：读取只经过运行时实体状态端口。</summary>
internal sealed class RuntimeQueueRepository : IQueueRepository
{
    private readonly AutomationDefinitionState _state;

    public RuntimeQueueRepository(AutomationDefinitionState state)
    {
        _state = state;
    }

    public DispatchQueue? FindById(string id) => _state.FindQueue(id);

    public IReadOnlyList<DispatchQueue> Snapshot() => _state.SnapshotQueues();
}

/// <summary>运行时执行快照适配器：由实体状态在同一同步边界内复制执行输入。</summary>
internal sealed class RuntimeExecutionSnapshotProvider : IExecutionSnapshotProvider
{
    private readonly AutomationDefinitionState _state;

    public RuntimeExecutionSnapshotProvider(AutomationDefinitionState state)
    {
        _state = state;
    }

    public ExecutionScriptSnapshot? SnapshotScript(string scriptId) => _state.SnapshotScriptForExecution(scriptId);

    public ExecutionQueueSnapshot? SnapshotQueue(string queueId) => _state.SnapshotQueueForExecution(queueId);
}

/// <summary>
/// 用户读取仓储：以全局用户快照为唯一数据源。
/// </summary>
internal sealed class RuntimeUserRepository : IUserRepository
{
    private readonly AutomationDefinitionState _state;

    public RuntimeUserRepository(AutomationDefinitionState state)
    {
        _state = state;
    }

    public ResolvedScriptUser? ResolveBinding(
        ScriptInstance script,
        string? userReference,
        IReadOnlyList<NexusUser>? users = null)
    {
        if (string.IsNullOrWhiteSpace(userReference))
        {
            return null;
        }

        List<NexusUser> source = Source(users).OrderBy(item => item.Index).ToList();
        NexusUser? user = source.FirstOrDefault(item =>
                string.Equals(item.Id, userReference, StringComparison.OrdinalIgnoreCase))
            ?? source.FirstOrDefault(item =>
                string.Equals(item.Name, userReference, StringComparison.OrdinalIgnoreCase));
        if (user is null)
        {
            return null;
        }

        UserScriptBinding? binding = user.Bindings.FirstOrDefault(item =>
            string.Equals(item.ScriptInstanceId, script.Id, StringComparison.Ordinal));
        return binding is null
            ? null
            : new ResolvedScriptUser(user.Id, user.Name, binding.Clone());
    }

    public ResolvedScriptUser? ResolveEnabledBinding(
        ScriptInstance script,
        string? userName,
        IReadOnlyList<NexusUser>? users = null)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }
        foreach (NexusUser user in Source(users).OrderBy(item => item.Index))
        {
            if (!string.Equals(user.Name, userName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            UserScriptBinding? binding = ResolveParticipatingBinding(script, user);
            return binding is null ? null : new ResolvedScriptUser(user.Id, user.Name, binding);
        }
        return null;
    }

    public IReadOnlyList<ResolvedScriptUser> ResolveEnabledBindings(
        ScriptInstance script,
        IReadOnlyList<NexusUser>? users = null)
    {
        return Source(users)
            .OrderBy(user => user.Index)
            .Select(user => new
            {
                User = user,
                Binding = ResolveParticipatingBinding(script, user),
            })
            .Where(item => item.Binding is not null)
            .Select(item => new ResolvedScriptUser(item.User.Id, item.User.Name, item.Binding!))
            .ToList();
    }

    private List<NexusUser> Source(IReadOnlyList<NexusUser>? users)
    {
        return users?.Select(user => user.Clone()).ToList() ?? _state.SnapshotUsers();
    }

    private static UserScriptBinding? ResolveParticipatingBinding(ScriptInstance script, NexusUser user)
    {
        return user.Bindings
            .Select(item => UserBindingOverrideResolver.Resolve(user, item))
            .FirstOrDefault(item =>
                item.Participates && string.Equals(item.ScriptInstanceId, script.Id, StringComparison.Ordinal));
    }
}

internal sealed class RuntimeUserSnapshotReader : IUserSnapshotReader
{
    private readonly AutomationDefinitionState _state;

    public RuntimeUserSnapshotReader(AutomationDefinitionState state)
    {
        _state = state;
    }

    public NexusUser? FindById(string id) => _state.FindUser(id);

    public IReadOnlyList<NexusUser> Snapshot() => _state.SnapshotUsers();
}

internal sealed class RuntimeUserMutationState : IUserMutationState
{
    private readonly AutomationDefinitionState _state;

    public RuntimeUserMutationState(AutomationDefinitionState state)
    {
        _state = state;
    }

    public NexusUser? Find(string id) => _state.FindUser(id);

    public IReadOnlyList<NexusUser> Snapshot() => _state.SnapshotUsers();

    public void Mutate(Action<UserMutationContext> mutation)
    {
        _state.Mutate(state => mutation(new UserMutationContext(state.Users)));
    }

    public TResult Mutate<TResult>(Func<UserMutationContext, TResult> mutation)
    {
        return _state.Mutate(state => mutation(new UserMutationContext(state.Users)));
    }
}

internal sealed class RuntimeSettingsProvider : ISettingsProvider
{
    private readonly Func<AppSettings> _current;

    public RuntimeSettingsProvider(Func<AppSettings> current)
    {
        _current = current;
    }

    public AppSettings Current => _current();
}

/// <summary>
/// 运行天数写入器：调度器每日首次 tick 时把 RunDays &gt; 0 的绑定减 1，
/// 减至 0 的绑定不再参与运行（Participates = false）。写入在数据锁与持久化路径内完成。
/// </summary>
internal sealed class RuntimeUserRunDaysWriter : IUserRunDaysMaintenance
{
    private readonly AutomationDefinitionState _state;
    private readonly Action<List<NexusUser>> _saveUsers;

    public RuntimeUserRunDaysWriter(
        AutomationDefinitionState state,
        Action<List<NexusUser>> saveUsers)
    {
        _state = state;
        _saveUsers = saveUsers;
    }

    public bool DecrementDaily()
    {
        return _state.Mutate(mutation =>
        {
            bool changed = false;
            foreach (NexusUser user in mutation.Users)
            {
                UserBindingOverrides overrides = user.BindingOverrides ?? new UserBindingOverrides();
                if (overrides.General?.SyncEnabled == true)
                {
                    if (overrides.General.RunDays > 0)
                    {
                        overrides.General.RunDays--;
                        changed = true;
                    }
                    continue;
                }
                foreach (UserScriptBinding binding in user.Bindings)
                {
                    if (binding.RunDays > 0)
                    {
                        binding.RunDays--;
                        changed = true;
                    }
                }
            }
            if (changed)
            {
                _saveUsers(mutation.Users);
            }
            return changed;
        });
    }
}

/// <summary>脚本写入状态端口：只暴露脚本集合，不把跨实体 mutation context 传入模块。</summary>
internal sealed class RuntimeScriptMutationState : IScriptMutationState
{
    private readonly AutomationDefinitionState _state;

    public RuntimeScriptMutationState(AutomationDefinitionState state)
    {
        _state = state;
    }

    public ScriptInstance? Find(string id) => _state.FindScript(id);

    public IReadOnlyList<ScriptInstance> Snapshot() => _state.SnapshotScripts();

    public void Mutate(Action<IList<ScriptInstance>> mutation)
    {
        _state.Mutate(state => mutation(state.Scripts));
    }

    public TResult Mutate<TResult>(Func<IList<ScriptInstance>, TResult> mutation)
    {
        return _state.Mutate(state => mutation(state.Scripts));
    }
}

/// <summary>队列写入状态端口：只暴露队列集合。</summary>
internal sealed class RuntimeQueueMutationState : IQueueMutationState
{
    private readonly AutomationDefinitionState _state;

    public RuntimeQueueMutationState(AutomationDefinitionState state)
    {
        _state = state;
    }

    public DispatchQueue? Find(string id) => _state.FindQueue(id);

    public IReadOnlyList<DispatchQueue> Snapshot() => _state.SnapshotQueues();

    public void Mutate(Action<IList<DispatchQueue>> mutation)
    {
        _state.Mutate(state => mutation(state.Queues));
    }

    public TResult Mutate<TResult>(Func<IList<DispatchQueue>, TResult> mutation)
    {
        return _state.Mutate(state => mutation(state.Queues));
    }
}

/// <summary>Scheduler only receives a copied queue snapshot for plan construction.</summary>
internal sealed class RuntimeQueueScheduleSnapshotReader : IQueueScheduleSnapshotReader
{
    private readonly IQueueRepository _queues;

    public RuntimeQueueScheduleSnapshotReader(IQueueRepository queues)
    {
        _queues = queues;
    }

    public IReadOnlyList<DispatchQueue> SnapshotForScheduling() => _queues.Snapshot();
}

/// <summary>Host adapter for the two scheduler invalidation ports.</summary>
internal sealed class RuntimePlansChanged : IScriptPlansChanged, IQueuePlansChanged, IUserPlansChanged
{
    private readonly Scheduler _scheduler;

    public RuntimePlansChanged(Scheduler scheduler)
    {
        _scheduler = scheduler;
    }

    public void RevalidatePendingPlans() => _scheduler.RevalidatePendingPlans();
}
