using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Plugins;

namespace NexusPipeline.Modules.Plugins.Runtime;

internal sealed class PluginEmulatorSupportRegistry
{
    private sealed record Registration(
        Guid Token,
        string PluginName,
        string ProviderId,
        int Priority,
        IPluginEmulatorSupportProvider Provider);

    private const int MaxProvidersPerPlugin = 16;
    private const int MaxPriority = 10_000;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Registration> _registrations = new();

    public IDisposable Register(string pluginName, IPluginEmulatorSupportProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        string id = provider.Id?.Trim() ?? "";
        if (!IsValidId(id))
        {
            throw new ArgumentException("模拟器 provider ID 必须是小写 kebab-case。", nameof(provider));
        }
        int priority = provider.Priority;
        if (priority is < -MaxPriority or > MaxPriority)
        {
            throw new ArgumentOutOfRangeException(nameof(provider), "模拟器 provider 优先级超出允许范围。");
        }

        Guid token = Guid.NewGuid();
        lock (_sync)
        {
            if (_registrations.Values.Count(item =>
                    string.Equals(item.PluginName, pluginName, StringComparison.OrdinalIgnoreCase)) >= MaxProvidersPerPlugin)
            {
                throw new InvalidOperationException("单个插件注册的模拟器 provider 数量超过上限。");
            }
            if (_registrations.Values.Any(item =>
                string.Equals(item.PluginName, pluginName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ProviderId, id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"模拟器 provider ID 重复：{pluginName}/{id}");
            }
            _registrations[token] = new Registration(token, pluginName, id, priority, provider);
        }
        return new CallbackDisposable(() => Remove(token));
    }

    public IReadOnlyList<EmulatorSupportProviderDescriptor> Snapshot(Func<string, bool> isEnabled)
    {
        lock (_sync)
        {
            return _registrations.Values
                .Where(item => isEnabled(item.PluginName))
                .OrderBy(item => item.Priority)
                .ThenBy(item => item.PluginName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ProviderId, StringComparer.OrdinalIgnoreCase)
                .Select(item => new EmulatorSupportProviderDescriptor(
                    item.PluginName,
                    item.ProviderId,
                    item.Priority,
                    item.Provider))
                .ToArray();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _registrations.Clear();
        }
    }

    private void Remove(Guid token)
    {
        lock (_sync)
        {
            _registrations.Remove(token);
        }
    }

    private static bool IsValidId(string id)
    {
        if (id.Length is 0 or > 64 || id[0] == '-' || id[^1] == '-')
        {
            return false;
        }
        bool previousHyphen = false;
        foreach (char character in id)
        {
            if (character == '-')
            {
                if (previousHyphen) return false;
                previousHyphen = true;
                continue;
            }
            if (!((character >= 'a' && character <= 'z')
                || (character >= '0' && character <= '9')))
            {
                return false;
            }
            previousHyphen = false;
        }
        return true;
    }
}
