using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

/// <summary>插件统一元数据投影（前端插件列表 / 新建专项脚本选择卡片）。</summary>
internal sealed record PluginSummary(
    string Name, string DisplayName, string GameName, string Description,
    string Version, bool IsBuiltIn, string Kind, bool SupportsEmulator);

/// <summary>插件生命周期管理：内置 C# 插件（notify）+ 数据化专项插件（plugins/&lt;名称&gt;/plugin.json）发现、加载、启用开关、能力查询。</summary>
internal sealed class PluginManager
{
    private readonly List<IPlugin> _plugins = new();

    private readonly List<DataSpecializedPlugin> _dataPlugins = new();

    /// <summary>全部已启用的通知通道（内置通道；数据化插件无代码不参与通知）。</summary>
    public IReadOnlyList<INotifyChannel> NotifyChannels =>
        _plugins.Where(p => p is INotifyChannel && IsEnabled(p.Name))
            .Cast<INotifyChannel>()
            .ToList();

    /// <summary>插件统一元数据投影（内置 general + 数据化 specialized）。</summary>
    public IReadOnlyList<PluginSummary> PluginSummaries
    {
        get
        {
            var list = new List<PluginSummary>();
            foreach (IPlugin plugin in _plugins)
            {
                list.Add(new PluginSummary(plugin.Name, plugin.DisplayName, "", plugin.Description, plugin.Version, plugin.IsBuiltIn, "general", false));
            }
            foreach (DataSpecializedPlugin plugin in _dataPlugins)
            {
                list.Add(new PluginSummary(plugin.Name, plugin.DisplayName, plugin.GameName, plugin.Description, plugin.Version, plugin.IsBuiltIn, "specialized", plugin.SupportsEmulator));
            }
            return list;
        }
    }

    /// <summary>专项插件是否支持安卓模拟器启动方式（v0.7.0+，由 plugin.json 的 supportsEmulator 声明，缺省 false）。</summary>
    public bool SupportsEmulator(string pluginName)
    {
        DataSpecializedPlugin? plugin = _dataPlugins.FirstOrDefault(p => string.Equals(p.Name, pluginName, StringComparison.OrdinalIgnoreCase));
        return plugin?.SupportsEmulator ?? false;
    }

    /// <summary>调用数据化专项插件按根目录推导配置快照；插件不存在/未启用/推导失败返回 null。</summary>
    public ScriptProfile? ResolveProfile(string pluginName, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(pluginName) || string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }
        DataSpecializedPlugin? plugin = _dataPlugins.FirstOrDefault(p => string.Equals(p.Name, pluginName, StringComparison.OrdinalIgnoreCase));
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
            Logger.Warn($"[插件] 数据化插件「{plugin.DisplayName}」解析「{rootPath}」失败：{ex.Message}");
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

    /// <summary>加载全部插件（v0.6.6+ 幂等：重复调用先清空，避免重复注册）。</summary>
    public void LoadAll()
    {
        _plugins.Clear();
        _dataPlugins.Clear();
        foreach (IPlugin plugin in DiscoverBuiltIn())
        {
            _plugins.Add(plugin);
        }
        foreach (DataSpecializedPlugin plugin in DiscoverDataPlugins())
        {
            _dataPlugins.Add(plugin);
        }
        PruneUnknownPluginSettings();
        foreach (IPlugin plugin in _plugins)
        {
            bool enabled = IsEnabled(plugin.Name);
            if (enabled)
            {
                try
                {
                    plugin.Initialize(new PluginContext(plugin.Name));
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
        foreach (DataSpecializedPlugin plugin in _dataPlugins)
        {
            Logger.Info($"[插件] 已{(IsEnabled(plugin.Name) ? "启用" : "禁用")}：{plugin.DisplayName} v{plugin.Version}（数据化）");
        }
    }

    private void PruneUnknownPluginSettings()
    {
        AppSettings settings = RuntimeContext.Instance.Settings;
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IPlugin plugin in _plugins)
        {
            known.Add(plugin.Name);
        }
        foreach (DataSpecializedPlugin plugin in _dataPlugins)
        {
            known.Add(plugin.Name);
        }
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
            catch (Exception ex)
            {
                Logger.Warn($"插件「{plugin.DisplayName}」关停失败：{ex.Message}");
            }
        }
    }

    public bool IsEnabled(string name)
    {
        AppSettings settings = RuntimeContext.Instance.Settings;
        if (_plugins.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return settings.EnabledPlugins.Contains(name, StringComparer.OrdinalIgnoreCase);
        }
        if (!_dataPlugins.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        // 数据化专项插件：外部默认启用，显式禁用记入 DisabledPlugins（重启后仍禁用）。
        return !settings.DisabledPlugins.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    public void SetEnabled(string name, bool enabled, string source = Audit.System)
    {
        AppSettings settings = RuntimeContext.Instance.Settings;
        // v0.7.4（KN-24）：插件不存在时显式拒绝（此前静默写入配置，待下次 LoadAll 的 PruneUnknownPluginSettings 才清理）。
        bool isBuiltIn = _plugins.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        bool isDataPlugin = _dataPlugins.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (!isBuiltIn && !isDataPlugin)
        {
            Logger.Warn($"[插件] 插件「{name}」不存在，已忽略启用开关操作。");
            return;
        }
        // 内置插件禁用同样写入 DisabledPlugins——非纯冗余：ConfigStore.Normalize 的
        // 「旧配置补默认内置插件（emulator-adapter）」判据依赖它标记「用户显式禁用过」，迁移时不再补回；
        // IsEnabled 对内置插件只查 EnabledPlugins 白名单。
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
        return new List<IPlugin>
        {
            new NotifyPlugin(),
            new EmulatorAdapterPlugin(),
        };
    }

    /// <summary>发现数据化专项插件：plugins/ 下每个含有效 plugin.json 的子目录注册一个插件；无效目录仅警告。</summary>
    private static List<DataSpecializedPlugin> DiscoverDataPlugins()
    {
        var list = new List<DataSpecializedPlugin>();
        if (!Directory.Exists(AppPaths.PluginsDir))
        {
            return list;
        }
        foreach (string dir in Directory.GetDirectories(AppPaths.PluginsDir))
        {
            DataSpecializedPlugin? plugin = DataSpecializedPlugin.Load(dir);
            if (plugin is not null)
            {
                list.Add(plugin);
            }
            else
            {
                Logger.Warn($"[插件] 跳过无效插件目录：{Path.GetFileName(dir)}（缺少 plugin.json 或 data 引用无效）");
            }
        }
        return list;
    }
}
