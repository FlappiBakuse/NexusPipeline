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

internal sealed class RuntimeUserRepository : IUserRepository
{
    private readonly Action<Action> _withDataLock;

    public RuntimeUserRepository(Action<Action> withDataLock)
    {
        _withDataLock = withDataLock;
    }

    public ScriptUser? FindEnabled(ScriptInstance script, string? userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }
        ScriptUser? result = null;
        _withDataLock(() =>
        {
            result = script.Users.FirstOrDefault(user => user.Enabled
                && string.Equals(user.Name, userName, StringComparison.OrdinalIgnoreCase));
        });
        return result;
    }

    public IReadOnlyList<string> EnabledNames(ScriptInstance script)
    {
        List<string> result = new();
        _withDataLock(() => result = script.Users.Where(user => user.Enabled).Select(user => user.Name).ToList());
        return result;
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
