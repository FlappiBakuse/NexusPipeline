using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.Modules.Plugins.Runtime;

/// <summary>Registration lifetime follows the managed plugin runtime lifetime.</summary>
internal sealed class PluginExecutionProviderRegistry
{
    private sealed record Registration(Guid Token, string PluginName, IPluginExecutionProvider Provider);
    private readonly object _sync = new();
    private readonly Dictionary<string, Registration> _providers = new(StringComparer.Ordinal);
    private readonly IPluginConfigurationMutationGate? _configurationGate;
    public PluginExecutionProviderRegistry(IPluginConfigurationMutationGate? configurationGate = null) => _configurationGate = configurationGate;

    public IDisposable? TryAcquireConfiguration(string plugin, string script, string user, string root)
    {
        lock (_sync) if (!_providers.ContainsKey(plugin)) return null;
        if (string.IsNullOrWhiteSpace(script) || string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        return _configurationGate?.TryAcquireProviderConfiguration(script, user, Path.GetFullPath(root));
    }

    public IDisposable Register(string pluginName, IPluginExecutionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(pluginName)
            || !string.Equals(pluginName, provider.Id, StringComparison.Ordinal)
            || !IsValidId(provider.Id))
            throw new ArgumentException("执行 provider ID 必须与拥有它的插件 ID 完全相同，且为小写 kebab-case。", nameof(provider));
        Guid token = Guid.NewGuid();
        lock (_sync)
        {
            if (_providers.ContainsKey(provider.Id))
                throw new InvalidOperationException($"执行 provider ID 重复：{provider.Id}");
            _providers.Add(provider.Id, new Registration(token, pluginName, provider));
        }
        return new CallbackDisposable(() =>
        {
            lock (_sync)
            {
                if (_providers.TryGetValue(provider.Id, out Registration? active) && active.Token == token)
                    _providers.Remove(provider.Id);
            }
        });
    }

    public IPluginExecutionProvider? Resolve(string providerId, Func<string, bool> isEnabled)
    {
        lock (_sync)
        {
            return _providers.TryGetValue(providerId, out Registration? registration)
                && isEnabled(registration.PluginName) ? registration.Provider : null;
        }
    }

    public void Clear()
    {
        lock (_sync) _providers.Clear();
    }

    private static bool IsValidId(string id)
    {
        if (id.Length is 0 or > 64 || id[0] == '-' || id[^1] == '-') return false;
        bool hyphen = false;
        foreach (char c in id)
        {
            if (c == '-')
            {
                if (hyphen) return false;
                hyphen = true;
                continue;
            }
            if (c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9')) return false;
            hyphen = false;
        }
        return true;
    }
}
