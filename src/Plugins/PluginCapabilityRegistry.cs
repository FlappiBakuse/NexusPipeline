using NexusPipeline.Extensibility;

namespace NexusPipeline.Plugins;

/// <summary>
/// 宿主内部 capability registry：C# 能力按接口注册，数据化能力按 key 映射。
/// LoadAll 先清空再注册，保证重复加载不会重复发现能力。
/// </summary>
internal sealed class PluginCapabilityRegistry
{
    private readonly List<(string PluginName, IPluginCapability Capability)> _typed = new();

    private readonly Dictionary<string, HashSet<string>> _declared = new(StringComparer.OrdinalIgnoreCase);

    public void Clear()
    {
        _typed.Clear();
        _declared.Clear();
    }

    public void Register(IPlugin plugin)
    {
        if (plugin is IPluginCapability capability)
        {
            _typed.Add((plugin.Name, capability));
        }
    }

    public void Register(string pluginName, IPluginCapability capability)
    {
        _typed.Add((pluginName, capability));
    }

    public void RegisterKeys(string pluginName, IEnumerable<string> keys)
    {
        if (!_declared.TryGetValue(pluginName, out HashSet<string>? set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _declared[pluginName] = set;
        }
        foreach (string key in keys)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                set.Add(key.Trim());
            }
        }
    }

    public IReadOnlyList<T> GetAll<T>(Func<string, bool> isEnabled) where T : class, IPluginCapability
    {
        return _typed
            .Where(item => isEnabled(item.PluginName))
            .Select(item => item.Capability)
            .OfType<T>()
            .ToList();
    }

    public T? Get<T>(string pluginName, Func<string, bool> isEnabled) where T : class, IPluginCapability
    {
        if (!isEnabled(pluginName))
        {
            return null;
        }
        return _typed
            .Where(item => string.Equals(item.PluginName, pluginName, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Capability)
            .OfType<T>()
            .FirstOrDefault();
    }

    public bool HasKey(string pluginName, string key, Func<string, bool> isEnabled)
    {
        return isEnabled(pluginName)
            && _declared.TryGetValue(pluginName, out HashSet<string>? keys)
            && keys.Contains(key);
    }
}
