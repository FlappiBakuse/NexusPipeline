using NexusPipeline.Modules.Scripts.Queries;
using NexusPipeline.Platform.Windows;

namespace NexusPipeline.Modules.Scripts;

/// <summary>脚本图标应用服务：负责脚本目标校验和按脚本实例缓存，图标读取交给 Platform。</summary>
internal sealed class ScriptIconService
{
    private readonly ScriptQueries _queries;
    private readonly ExecutableIconReader _reader;
    private readonly Dictionary<string, byte[]> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public ScriptIconService(ScriptQueries queries, ExecutableIconReader reader)
    {
        _queries = queries;
        _reader = reader;
    }

    public byte[]? Get(string scriptId)
    {
        ScriptInstance? script = _queries.FindEffective(scriptId);
        if (script is null || string.IsNullOrWhiteSpace(script.MainExe))
        {
            return null;
        }
        lock (_sync)
        {
            if (_cache.TryGetValue(scriptId, out byte[]? cached))
            {
                return cached;
            }
            byte[]? icon = _reader.Read(script.MainExe);
            if (icon is not null)
            {
                _cache[scriptId] = icon;
            }
            return icon;
        }
    }

    public void Invalidate(string scriptId)
    {
        lock (_sync)
        {
            _cache.Remove(scriptId);
        }
    }
}
