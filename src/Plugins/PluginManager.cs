using System.Reflection;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

/// <summary>插件生命周期管理：发现（内置 + plugins/*.dll）、加载、启用开关、能力查询。</summary>
internal sealed class PluginManager
{
    private readonly List<IPlugin> _plugins = new();

    public IReadOnlyList<IPlugin> Plugins => _plugins;

    /// <summary>全部已启用的通知通道（内置 + 外部 INotifyChannel 并存，多通道同时发送）。</summary>
    public IReadOnlyList<INotifyChannel> NotifyChannels =>
        _plugins.OfType<IPlugin>()
            .Where(p => p is INotifyChannel && IsEnabled(p.Name))
            .Cast<INotifyChannel>()
            .ToList();

    /// <summary>已启用的专用插件（专项脚本实例的适配能力来源）。</summary>
    public IReadOnlyList<ISpecializedScriptPlugin> SpecializedPlugins =>
        _plugins.OfType<ISpecializedScriptPlugin>().Where(p => IsEnabled(p.Name)).ToList();

    /// <summary>调用专用插件按根目录推导配置快照；插件不存在/未启用/推导失败返回 null。</summary>
    public ScriptProfile? ResolveProfile(string pluginName, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(pluginName) || string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }
        ISpecializedScriptPlugin? plugin = _plugins.OfType<ISpecializedScriptPlugin>()
            .FirstOrDefault(p => string.Equals(p.Name, pluginName, StringComparison.OrdinalIgnoreCase));
        if (plugin is null || !IsEnabled(plugin.Name))
        {
            return null;
        }
        try
        {
            return plugin.Resolve(rootPath.Trim());
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件] 专用插件「{plugin.DisplayName}」解析「{rootPath}」失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>向全部已启用通知通道分发脚本运行通知（多通道并存，单个通道失败不影响其余）。</summary>
    public async Task NotifyScriptAsync(ScriptInstance script, RunRecord record)
    {
        foreach (INotifyChannel channel in NotifyChannels)
        {
            try
            {
                await channel.NotifyScriptAsync(script, record).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[通知] 通道「{channelName(channel)}」发送脚本通知失败：{ex.Message}");
            }
        }
    }

    /// <summary>向全部已启用通知通道分发队列汇总通知（多通道并存，单个通道失败不影响其余）。</summary>
    public async Task NotifyQueueAsync(DispatchQueue queue, List<RunRecord> records)
    {
        foreach (INotifyChannel channel in NotifyChannels)
        {
            try
            {
                await channel.NotifyQueueAsync(queue, records).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[通知] 通道「{channelName(channel)}」发送队列通知失败：{ex.Message}");
            }
        }
    }

    private static string channelName(INotifyChannel channel)
    {
        return channel is IPlugin plugin ? plugin.DisplayName : channel.GetType().Name;
    }

    public void LoadAll()
    {
        foreach (IPlugin plugin in DiscoverBuiltIn())
        {
            _plugins.Add(plugin);
        }
        foreach (IPlugin plugin in DiscoverExternal())
        {
            _plugins.Add(plugin);
        }
        PruneUnknownPluginSettings();
        foreach (IPlugin plugin in _plugins)
        {
            bool enabled = IsEnabled(plugin.Name);
            if (enabled)
            {
                try
                {
                    plugin.Initialize(new PluginContext());
                    Logger.Info($"[插件] 已启用：{plugin.DisplayName} v{plugin.Version}");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[插件] 插件「{plugin.DisplayName}」初始化失败：{ex.Message}");
                }
            }
            else
            {
                Logger.Info($"[插件] 已禁用：{plugin.DisplayName}");
            }
        }
    }

    private void PruneUnknownPluginSettings()
    {
        AppSettings settings = RuntimeContext.Instance.Settings;
        var known = new HashSet<string>(_plugins.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        int before = settings.EnabledPlugins.Count + settings.DisabledPlugins.Count;
        settings.EnabledPlugins.RemoveAll(name => !known.Contains(name));
        settings.DisabledPlugins.RemoveAll(name => !known.Contains(name));
        if (settings.EnabledPlugins.Count + settings.DisabledPlugins.Count != before)
        {
            ConfigStore.Save(settings);
            Logger.Info("[插件] 已清理设置中不存在的插件名。");
        }
    }

    public void ShutdownAll()
    {
        foreach (IPlugin plugin in _plugins)
        {
            try
            {
                plugin.Shutdown();
            }
            catch
            {
            }
        }
    }

    public bool IsEnabled(string name)
    {
        IPlugin? plugin = _plugins.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (plugin is null)
        {
            return false;
        }
        AppSettings settings = RuntimeContext.Instance.Settings;
        if (plugin.IsBuiltIn)
        {
            return settings.EnabledPlugins.Contains(name, StringComparer.OrdinalIgnoreCase);
        }
        return !settings.DisabledPlugins.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    public void SetEnabled(string name, bool enabled, string source = Audit.System)
    {
        AppSettings settings = RuntimeContext.Instance.Settings;
        bool exists = settings.EnabledPlugins.Contains(name, StringComparer.OrdinalIgnoreCase);
        if (enabled && !exists)
        {
            settings.EnabledPlugins.Add(name);
        }
        else if (!enabled && exists)
        {
            settings.EnabledPlugins.RemoveAll(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        }
        if (enabled)
        {
            settings.DisabledPlugins.RemoveAll(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        }
        else if (!settings.DisabledPlugins.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            settings.DisabledPlugins.Add(name);
        }
        ConfigStore.Save(settings);
        Audit.Log(source, $"{(enabled ? "启用" : "禁用")}插件", name);
        Logger.Info($"[插件] 已{(enabled ? "启用" : "禁用")}：{name}（重启后生效）。");
    }

    private static List<IPlugin> DiscoverBuiltIn()
    {
        var list = new List<IPlugin>
        {
            new NotifyPlugin(),
        };
        return list;
    }

    private static List<IPlugin> DiscoverExternal()
    {
        var list = new List<IPlugin>();
        if (!Directory.Exists(AppPaths.PluginsDir))
        {
            return list;
        }
        foreach (string dll in Directory.GetFiles(AppPaths.PluginsDir, "*.dll"))
        {
            try
            {
                Assembly assembly = Assembly.LoadFrom(dll);
                foreach (Type type in assembly.GetTypes())
                {
                    if (typeof(IPlugin).IsAssignableFrom(type) && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) is not null)
                    {
                        if (Activator.CreateInstance(type) is IPlugin plugin)
                        {
                            list.Add(plugin);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[插件] 加载 {Path.GetFileName(dll)} 失败：{ex.Message}");
            }
        }
        return list;
    }
}
