using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Services;

namespace NexusPipeline.App.Repositories;

/// <summary>运行时脚本仓储适配器：保留现有共享列表和磁盘写入协议，只把读取依赖显式化。</summary>
internal sealed class RuntimeScriptRepository : IScriptRepository
{
    private readonly Func<string, ScriptInstance?> _find;
    private readonly Func<List<ScriptInstance>> _snapshot;

    public RuntimeScriptRepository(Func<string, ScriptInstance?> find, Func<List<ScriptInstance>> snapshot)
    {
        _find = find;
        _snapshot = snapshot;
    }

    public ScriptInstance? FindById(string id) => _find(id);

    public IReadOnlyList<ScriptInstance> Snapshot() => _snapshot();
}

/// <summary>运行时队列仓储适配器：写入行为仍由现有 Web/CLI 事务路径控制。</summary>
internal sealed class RuntimeQueueRepository : IQueueRepository
{
    private readonly Func<string, DispatchQueue?> _find;
    private readonly Func<List<DispatchQueue>> _snapshot;

    public RuntimeQueueRepository(Func<string, DispatchQueue?> find, Func<List<DispatchQueue>> snapshot)
    {
        _find = find;
        _snapshot = snapshot;
    }

    public DispatchQueue? FindById(string id) => _find(id);

    public IReadOnlyList<DispatchQueue> Snapshot() => _snapshot();
}

/// <summary>运行时执行快照适配器：由组合根在一次 DataLock 内复制队列与脚本引用。</summary>
internal sealed class RuntimeExecutionSnapshotProvider : IExecutionSnapshotProvider
{
    private readonly Func<string, ExecutionScriptSnapshot?> _snapshotScript;
    private readonly Func<string, ExecutionQueueSnapshot?> _snapshotQueue;

    public RuntimeExecutionSnapshotProvider(
        Func<string, ExecutionScriptSnapshot?> snapshotScript,
        Func<string, ExecutionQueueSnapshot?> snapshotQueue)
    {
        _snapshotScript = snapshotScript;
        _snapshotQueue = snapshotQueue;
    }

    public ExecutionScriptSnapshot? SnapshotScript(string scriptId) => _snapshotScript(scriptId);

    public ExecutionQueueSnapshot? SnapshotQueue(string queueId) => _snapshotQueue(queueId);
}

/// <summary>
/// 用户读取仓储：以全局用户快照为唯一数据源（启动时的 UserModelMigration 保证脚本不再携带嵌套用户）。
/// </summary>
internal sealed class RuntimeUserRepository : IUserRepository
{
    private readonly Func<List<NexusUser>> _snapshotUsers;

    public RuntimeUserRepository(Func<List<NexusUser>> snapshotUsers)
    {
        _snapshotUsers = snapshotUsers;
    }

    public ScriptUser? FindEnabled(ScriptInstance script, string? userName)
    {
        return ResolveEnabledBinding(script, userName)?.ToLegacyScriptUser();
    }

    public IReadOnlyList<string> EnabledNames(ScriptInstance script)
    {
        return _snapshotUsers()
            .OrderBy(user => user.Index)
            .Select(user => new
            {
                user.Name,
                Binding = ResolveParticipatingBinding(script, user),
            })
            .Where(item => item.Binding is not null)
            .Select(item => item.Name)
            .ToList();
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
        return users?.Select(user => user.Clone()).ToList() ?? _snapshotUsers();
    }

    private static UserScriptBinding? ResolveParticipatingBinding(ScriptInstance script, NexusUser user)
    {
        return user.Bindings
            .Select(item => UserBindingOverrideResolver.Resolve(user, item))
            .FirstOrDefault(item =>
                item.Participates && string.Equals(item.ScriptInstanceId, script.Id, StringComparison.Ordinal));
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
internal sealed class RuntimeUserRunDaysWriter
{
    private readonly Action<Action> _withDataLock;
    private readonly Func<List<NexusUser>> _snapshotUsers;
    private readonly Action<List<NexusUser>> _saveUsers;

    public RuntimeUserRunDaysWriter(
        Action<Action> withDataLock,
        Func<List<NexusUser>> snapshotUsers,
        Action<List<NexusUser>> saveUsers)
    {
        _withDataLock = withDataLock;
        _snapshotUsers = snapshotUsers;
        _saveUsers = saveUsers;
    }

    public bool DecrementDaily()
    {
        bool changed = false;
        _withDataLock(() =>
        {
            List<NexusUser> users = _snapshotUsers();
            foreach (NexusUser user in users)
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
                _saveUsers(users);
            }
        });
        return changed;
    }
}
