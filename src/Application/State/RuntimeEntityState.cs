using NexusPipeline.Models;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Services.Execution;

namespace NexusPipeline.App.State;

/// <summary>
/// 运行时实体的唯一内存所有权与同步边界。
/// 状态层只负责实体快照、查找、替换和原子 mutation，不承载业务规则或持久化。
/// </summary>
internal sealed class RuntimeEntityState
{
    private readonly object _sync = new();
    private List<ScriptInstance> _scripts = new();
    private List<DispatchQueue> _queues = new();
    private List<NexusUser> _users = new();

    public bool LastScriptsLoadWasAuthoritative { get; private set; }

    public void ReplaceLoadedState(
        IEnumerable<ScriptInstance> scripts,
        IEnumerable<DispatchQueue> queues,
        IEnumerable<NexusUser> users,
        bool scriptsAuthoritative)
    {
        lock (_sync)
        {
            _scripts = scripts.Select(item => item.Clone()).ToList();
            _queues = queues.Select(item => item.Clone()).ToList();
            _users = users.Select(item => item.Clone()).ToList();
            LastScriptsLoadWasAuthoritative = scriptsAuthoritative;
        }
    }

    public ScriptInstance? FindScript(string id)
    {
        lock (_sync)
        {
            return _scripts.FirstOrDefault(item => item.Id == id)?.Clone();
        }
    }

    public DispatchQueue? FindQueue(string id)
    {
        lock (_sync)
        {
            return _queues.FirstOrDefault(item => item.Id == id)?.Clone();
        }
    }

    public NexusUser? FindUser(string id)
    {
        lock (_sync)
        {
            return _users.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))?.Clone();
        }
    }

    public List<ScriptInstance> SnapshotScripts()
    {
        lock (_sync)
        {
            return _scripts.Select(item => item.Clone()).ToList();
        }
    }

    public List<DispatchQueue> SnapshotQueues()
    {
        lock (_sync)
        {
            return _queues.Select(item => item.Clone()).ToList();
        }
    }

    public List<NexusUser> SnapshotUsers()
    {
        lock (_sync)
        {
            return _users.Select(item => item.Clone()).ToList();
        }
    }

    public void Mutate(Action<RuntimeEntityMutationContext> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        lock (_sync)
        {
            mutation(new RuntimeEntityMutationContext(_scripts, _queues, _users));
        }
    }

    public TResult Mutate<TResult>(Func<RuntimeEntityMutationContext, TResult> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        lock (_sync)
        {
            return mutation(new RuntimeEntityMutationContext(_scripts, _queues, _users));
        }
    }

    public ExecutionScriptSnapshot? SnapshotScriptForExecution(string id)
    {
        lock (_sync)
        {
            ScriptInstance? script = _scripts.FirstOrDefault(item => item.Id == id);
            return script is null
                ? null
                : new ExecutionScriptSnapshot(
                    script.Clone(),
                    _users.Select(item => item.Clone()).ToList());
        }
    }

    public ExecutionQueueSnapshot? SnapshotQueueForExecution(string id)
    {
        lock (_sync)
        {
            DispatchQueue? queue = _queues.FirstOrDefault(item => item.Id == id);
            if (queue is null)
            {
                return null;
            }
            return new ExecutionQueueSnapshot(
                queue.Clone(),
                _scripts.Select(item => item.Clone()).ToList(),
                _users.Select(item => item.Clone()).ToList());
        }
    }
}

/// <summary>一次实体 mutation 临界区内的受控可变集合视图，仅由 State 创建。</summary>
internal sealed class RuntimeEntityMutationContext
{
    internal RuntimeEntityMutationContext(
        List<ScriptInstance> scripts,
        List<DispatchQueue> queues,
        List<NexusUser> users)
    {
        Scripts = scripts;
        Queues = queues;
        Users = users;
    }

    internal List<ScriptInstance> Scripts { get; }

    internal List<DispatchQueue> Queues { get; }

    internal List<NexusUser> Users { get; }
}
