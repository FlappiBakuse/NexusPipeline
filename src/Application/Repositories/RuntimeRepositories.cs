using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;

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

internal sealed class RuntimeUserRepository : IUserRepository
{
    private readonly Action<Action> _withDataLock;
    private readonly Func<List<NexusUser>> _snapshotUsers;

    public RuntimeUserRepository(Action<Action> withDataLock, Func<List<NexusUser>> snapshotUsers)
    {
        _withDataLock = withDataLock;
        _snapshotUsers = snapshotUsers;
    }

    public ScriptUser? FindEnabled(ScriptInstance script, string? userName)
    {
        return ResolveEnabledBinding(script, userName)?.ToLegacyScriptUser();
    }

    public IReadOnlyList<string> EnabledNames(ScriptInstance script)
    {
        List<NexusUser> source = _snapshotUsers();
        List<string> result = source
            .OrderBy(user => user.Index)
            .Select(user => new
            {
                user.Name,
                Binding = user.Bindings.FirstOrDefault(item =>
                    item.Participates && string.Equals(item.ScriptInstanceId, script.Id, StringComparison.Ordinal)),
            })
            .Where(item => item.Binding is not null)
            .Select(item => item.Name)
            .ToList();
        if (result.Count > 0 || source.Count > 0 || script.Users.Count == 0)
        {
            return result;
        }
        List<string> legacy = new();
        _withDataLock(() => legacy = script.Users.Where(user => user.Enabled).Select(user => user.Name).ToList());
        return legacy;
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
        List<NexusUser> source = users?.Select(user => user.Clone()).ToList() ?? _snapshotUsers();
        foreach (NexusUser user in source.OrderBy(item => item.Index))
        {
            if (!string.Equals(user.Name, userName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            UserScriptBinding? binding = user.Bindings.FirstOrDefault(item =>
                item.Participates && string.Equals(item.ScriptInstanceId, script.Id, StringComparison.Ordinal));
            if (binding is not null)
            {
                return new ResolvedScriptUser(user.Id, user.Name, binding.Clone());
            }
            return null;
        }
        // 仅用于旧 fixture/尚未迁移的内存脚本；生产启动会先完成 迁移。
        ScriptUser? legacy = null;
        _withDataLock(() =>
        {
            legacy = script.Users.FirstOrDefault(item => item.Enabled
                && string.Equals(item.Name, userName, StringComparison.OrdinalIgnoreCase));
        });
        return legacy is null
            ? null
            : new ResolvedScriptUser(
                "",
                legacy.Name,
                new UserScriptBinding
                {
                    ScriptInstanceId = script.Id,
                    Enabled = legacy.Enabled,
                    PreRunScript = legacy.PreRunScript,
                    PreRunOnceOnly = legacy.PreRunOnceOnly,
                    PostRunScript = legacy.PostRunScript,
                    PostRunOnFinalOnly = legacy.PostRunOnFinalOnly,
                });
    }

    public IReadOnlyList<ResolvedScriptUser> ResolveEnabledBindings(
        ScriptInstance script,
        IReadOnlyList<NexusUser>? users = null)
    {
        List<NexusUser> source = users?.Select(user => user.Clone()).ToList() ?? _snapshotUsers();
        List<ResolvedScriptUser> result = source
            .OrderBy(user => user.Index)
            .Select(user => new
            {
                User = user,
                Binding = user.Bindings.FirstOrDefault(item =>
                    item.Participates && string.Equals(item.ScriptInstanceId, script.Id, StringComparison.Ordinal)),
            })
            .Where(item => item.Binding is not null)
            .Select(item => new ResolvedScriptUser(item.User.Id, item.User.Name, item.Binding!.Clone()))
            .ToList();
        if (result.Count > 0 || source.Count > 0 || script.Users.Count == 0)
        {
            return result;
        }
        List<ResolvedScriptUser> legacy = new();
        _withDataLock(() =>
        {
            legacy = script.Users
                .Where(user => user.Enabled)
                .Select(user => new ResolvedScriptUser(
                    "",
                    user.Name,
                    new UserScriptBinding
                    {
                        ScriptInstanceId = script.Id,
                        Enabled = user.Enabled,
                        PreRunScript = user.PreRunScript,
                        PreRunOnceOnly = user.PreRunOnceOnly,
                        PostRunScript = user.PostRunScript,
                        PostRunOnFinalOnly = user.PostRunOnFinalOnly,
                    }))
                .ToList();
        });
        return legacy;
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

/// <summary>配置交换恢复专用只读数据源适配器：脚本查找与用户快照由组合根装配注入。</summary>
internal sealed class RuntimeConfigRecoveryDataSource : IConfigRecoveryDataSource
{
    private readonly Func<string, ScriptInstance?> _findScript;
    private readonly Func<List<NexusUser>> _snapshotUsers;

    public RuntimeConfigRecoveryDataSource(
        Func<string, ScriptInstance?> findScript,
        Func<List<NexusUser>> snapshotUsers)
    {
        _findScript = findScript;
        _snapshotUsers = snapshotUsers;
    }

    public ScriptInstance? FindScript(string scriptId) => _findScript(scriptId);

    public IReadOnlyList<NexusUser> SnapshotUsers() => _snapshotUsers();
}

/// <summary>
/// 运行天数写入器：调度器每日首次 tick 时把 RunDays &gt; 0 的绑定减 1，
/// 减至 0 的绑定不再参与运行（Participates = false）。写入在数据锁与持久化路径内完成。
/// </summary>
internal sealed class RuntimeUserRunDaysWriter : IUserRunDaysWriter
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
