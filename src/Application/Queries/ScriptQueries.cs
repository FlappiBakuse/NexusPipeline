using NexusPipeline.App.State;
using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;

namespace NexusPipeline.App.Queries;

/// <summary>脚本读取用例：向适配层提供声明快照和当前插件解析后的展示快照。</summary>
internal sealed class ScriptQueries
{
    private readonly RuntimeEntityState _state;
    private readonly ScriptSpecResolver _resolver;

    public ScriptQueries(RuntimeEntityState state, ScriptSpecResolver resolver)
    {
        _state = state;
        _resolver = resolver;
    }

    public IReadOnlyList<ScriptInstance> ListEffective()
    {
        return _state.SnapshotScripts()
            .OrderBy(script => script.Index)
            .Select(_resolver.ResolveScript)
            .ToList();
    }

    public ScriptInstance? FindDeclaration(string id) => _state.FindScript(id);

    public ScriptInstance? FindEffective(string id)
    {
        ScriptInstance? declaration = _state.FindScript(id);
        return declaration is null ? null : _resolver.ResolveScript(declaration);
    }

    public ScriptInstance ResolveEffective(ScriptInstance declaration) => _resolver.ResolveScript(declaration);
}
