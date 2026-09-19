using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts.Resolution;
using NexusPipeline.Modules.Scripts;

namespace NexusPipeline.Modules.Scripts.Queries;

/// <summary>脚本读取用例：向适配层提供声明快照和当前插件解析后的展示快照。</summary>
internal sealed class ScriptQueries
{
    private readonly IScriptRepository _scripts;
    private readonly ScriptSpecResolver _resolver;

    public ScriptQueries(IScriptRepository scripts, ScriptSpecResolver resolver)
    {
        _scripts = scripts;
        _resolver = resolver;
    }

    public IReadOnlyList<ScriptInstance> ListEffective()
    {
        return _scripts.Snapshot()
            .OrderBy(script => script.Index)
            .Select(_resolver.ResolveScript)
            .ToList();
    }

    public ScriptInstance? FindDeclaration(string id) => _scripts.FindById(id);

    public ScriptInstance? FindEffective(string id)
    {
        ScriptInstance? declaration = _scripts.FindById(id);
        return declaration is null ? null : _resolver.ResolveScript(declaration);
    }

    public ScriptInstance ResolveEffective(ScriptInstance declaration) => _resolver.ResolveScript(declaration);
}
