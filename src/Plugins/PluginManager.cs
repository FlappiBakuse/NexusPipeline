using NexusPipeline.Models;
using NexusPipeline.Extensibility;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

/// <summary>插件统一元数据投影（前端插件列表 / 新建专项脚本选择卡片）。</summary>
internal sealed record PluginSummary(
    string Name, string DisplayName, string GameName, string Description,
    string Version, bool IsBuiltIn, string Kind);

internal enum PluginRuntimeState
{
    Discovered,
    Disabled,
    Initializing,
    Active,
    InitFailed,
    Shutdown,
}

/// <summary>插件生命周期管理：内置 C# 插件（notify）+ 数据化专项插件（plugins/&lt;名称&gt;/plugin.json）发现、加载、启用开关、能力查询。</summary>
internal sealed class PluginManager : INotificationChannelProvider, IEmulatorCapabilityProvider, IPluginCapabilityResolver
{
    private readonly List<IPlugin> _plugins = new();

    private readonly List<DataSpecializedPlugin> _dataPlugins = new();

    private readonly PluginCapabilityRegistry _capabilities = new();

    private readonly PluginHostServices _host;

    private readonly Func<List<IPlugin>> _discoverBuiltIn;

    private readonly Func<List<DataSpecializedPlugin>> _discoverData;

    private readonly Dictionary<string, bool> _configuredEnabled = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, PluginRuntimeState> _runtimeStates = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _runtimeErrors = new(StringComparer.OrdinalIgnoreCase);

    internal PluginManager(
        PluginHostServices host,
        Func<List<IPlugin>>? discoverBuiltIn = null,
        Func<List<DataSpecializedPlugin>>? discoverData = null)
    {
        _host = host;
        _discoverBuiltIn = discoverBuiltIn ?? DiscoverBuiltIn;
        _discoverData = discoverData ?? DiscoverDataPlugins;
    }

    /// <summary>全部已启用的通知通道（内置通道；数据化插件无代码不参与通知）。</summary>
    public IReadOnlyList<INotifyChannel> NotifyChannels =>
        _capabilities.GetAll<INotifyChannel>(IsRuntimeEnabled);

    public IReadOnlyList<INotifyChannel> GetNotificationChannels()
    {
        return NotifyChannels;
    }

    public bool IsEnabled()
    {
        return IsEnabled(AppSettings.EmulatorAdapterPlugin);
    }

    /// <summary>插件统一元数据投影（内置 general + 数据化 specialized）。</summary>
    public IReadOnlyList<PluginSummary> PluginSummaries
    {
        get
        {
            var list = new List<PluginSummary>();
            foreach (IPlugin plugin in _plugins)
            {
                list.Add(new PluginSummary(plugin.Name, plugin.DisplayName, "", plugin.Description, plugin.Version, plugin.IsBuiltIn, "general"));
            }
            foreach (DataSpecializedPlugin plugin in _dataPlugins)
            {
                list.Add(new PluginSummary(plugin.Name, plugin.DisplayName, plugin.GameName, plugin.Description, plugin.Version, plugin.IsBuiltIn, "specialized"));
            }
            return list;
        }
    }

    /// <summary>专项插件是否支持安卓模拟器启动方式（v0.7.0+，由 plugin.json 的 supportsEmulator 声明，缺省 false）。</summary>
    public bool SupportsEmulator(string pluginName)
    {
        return HasCapability(pluginName, PluginCapabilityKeys.Emulator);
    }

    /// <summary>按 key 查询数据化 capability；保留给旧 Web/限制门禁作为兼容 façade。</summary>
    public bool HasCapability(string pluginName, string capabilityKey)
    {
        return _capabilities.HasKey(pluginName, capabilityKey, IsRuntimeEnabled)
            || capabilityKey.Equals(PluginCapabilityKeys.Emulator, StringComparison.OrdinalIgnoreCase)
                && _capabilities.Get<IEmulatorCapability>(pluginName, IsRuntimeEnabled) is not null;
    }

    /// <summary>通用 C# capability 查询入口；新增能力无需在 PluginManager 增加类型分支。</summary>
    public IReadOnlyList<T> GetCapabilities<T>() where T : class, IPluginCapability
    {
        return _capabilities.GetAll<T>(IsRuntimeEnabled);
    }

    /// <summary>调用数据化专项插件按根目录推导配置快照；插件不存在/未启用/推导失败返回 null。</summary>
    public ScriptProfile? ResolveProfile(string pluginName, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(pluginName) || string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }
        IProfileResolver? resolver = _capabilities.Get<IProfileResolver>(pluginName, IsRuntimeEnabled);
        if (resolver is null)
        {
            return null;
        }
        try
        {
            return resolver.Resolve(rootPath.Trim());
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件] 插件「{pluginName}」解析「{rootPath}」失败：{ex.Message}");
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
        if (_plugins.Count > 0 || _dataPlugins.Count > 0)
        {
            ShutdownAll();
        }
        _plugins.Clear();
        _dataPlugins.Clear();
        _capabilities.Clear();
        _configuredEnabled.Clear();
        _runtimeStates.Clear();
        _runtimeErrors.Clear();
        foreach (IPlugin plugin in _discoverBuiltIn())
        {
            _plugins.Add(plugin);
            _capabilities.Register(plugin);
            _configuredEnabled[plugin.Name] = ReadConfiguredEnabled(plugin.Name, isBuiltIn: true);
            _runtimeStates[plugin.Name] = PluginRuntimeState.Discovered;
        }
        foreach (DataSpecializedPlugin plugin in _discoverData())
        {
            _dataPlugins.Add(plugin);
            _capabilities.Register(plugin.Name, plugin);
            _capabilities.RegisterKeys(plugin.Name, plugin.CapabilityKeys);
            _configuredEnabled[plugin.Name] = ReadConfiguredEnabled(plugin.Name, isBuiltIn: false);
            _runtimeStates[plugin.Name] = PluginRuntimeState.Discovered;
        }
        foreach (IPlugin plugin in _plugins)
        {
            bool enabled = _configuredEnabled[plugin.Name];
            if (enabled)
            {
                _runtimeStates[plugin.Name] = PluginRuntimeState.Initializing;
                try
                {
                    plugin.Initialize(new PluginContext(plugin.Name, _host));
                    _runtimeStates[plugin.Name] = PluginRuntimeState.Active;
                    Logger.Info($"[插件] 已启用：{plugin.DisplayName} v{plugin.Version}");
                }
                catch (Exception ex)
                {
                    _runtimeStates[plugin.Name] = PluginRuntimeState.InitFailed;
                    _runtimeErrors[plugin.Name] = ex.Message;
                    Logger.Warn($"[插件] 插件「{plugin.DisplayName}」初始化失败：{ex.Message}");
                }
            }
            else
            {
                _runtimeStates[plugin.Name] = PluginRuntimeState.Disabled;
                Logger.Info($"[插件] 已禁用：{plugin.DisplayName}");
            }
        }
        foreach (DataSpecializedPlugin plugin in _dataPlugins)
        {
            bool enabled = _configuredEnabled[plugin.Name];
            _runtimeStates[plugin.Name] = enabled ? PluginRuntimeState.Active : PluginRuntimeState.Disabled;
            Logger.Info($"[插件] 已{(enabled ? "启用" : "禁用")}：{plugin.DisplayName} v{plugin.Version}（数据化）");
        }
    }

    public void ShutdownAll()
    {
        foreach (IPlugin plugin in _plugins)
        {
            try
            {
                plugin.Shutdown();
                _runtimeStates[plugin.Name] = PluginRuntimeState.Shutdown;
            }
            catch (Exception ex)
            {
                Logger.Warn($"插件「{plugin.DisplayName}」关停失败：{ex.Message}");
            }
            finally
            {
                _runtimeStates[plugin.Name] = PluginRuntimeState.Shutdown;
            }
        }
        foreach (DataSpecializedPlugin plugin in _dataPlugins)
        {
            _runtimeStates[plugin.Name] = PluginRuntimeState.Shutdown;
        }
    }

    public bool IsEnabled(string name)
    {
        return IsRuntimeEnabled(name);
    }

    /// <summary>配置开关状态；开关写入后运行中能力保持原状，下一次加载才应用。</summary>
    public bool IsConfiguredEnabled(string name)
    {
        return _configuredEnabled.TryGetValue(name, out bool enabled)
            ? enabled
            : IsKnownPlugin(name) && ReadConfiguredEnabled(name, IsBuiltIn(name));
    }

    public string GetRuntimeState(string name)
    {
        return _runtimeStates.TryGetValue(name, out PluginRuntimeState state)
            ? state.ToString()
            : PluginRuntimeState.Discovered.ToString();
    }

    public string? GetRuntimeError(string name)
    {
        return _runtimeErrors.TryGetValue(name, out string? error) ? error : null;
    }

    public bool IsKnownPlugin(string name)
    {
        return _plugins.Any(plugin => string.Equals(plugin.Name, name, StringComparison.OrdinalIgnoreCase))
            || _dataPlugins.Any(plugin => string.Equals(plugin.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public bool SetEnabled(string name, bool enabled, string source = Audit.System)
    {
        AppSettings settings = _host.Settings;
        bool isBuiltIn = IsBuiltIn(name);
        bool isDataPlugin = _dataPlugins.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (!isBuiltIn && !isDataPlugin)
        {
            Logger.Warn($"[插件] 插件「{name}」不存在，已忽略启用开关操作。");
            return false;
        }
            // 内置插件禁用时同步写入 DisabledPlugins，ConfigStore.Normalize 据此识别用户显式禁用过
            // emulator-adapter，迁移时保留该选择；运行态能力查询由 _runtimeStates 决定。
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
        _configuredEnabled[name] = enabled;
        Audit.Log(source, $"{(enabled ? "启用" : "禁用")}插件", name);
        Logger.Info($"[插件] 已{(enabled ? "启用" : "禁用")}：{name}（重启后生效）。");
        return true;
    }

    private bool IsRuntimeEnabled(string name)
    {
        return _runtimeStates.TryGetValue(name, out PluginRuntimeState state)
            && state == PluginRuntimeState.Active;
    }

    private bool IsBuiltIn(string name)
    {
        return _plugins.Any(plugin => string.Equals(plugin.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private bool ReadConfiguredEnabled(string name, bool isBuiltIn)
    {
        AppSettings settings = _host.Settings;
        return isBuiltIn
            ? settings.EnabledPlugins.Contains(name, StringComparer.OrdinalIgnoreCase)
            : !settings.DisabledPlugins.Contains(name, StringComparer.OrdinalIgnoreCase);
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
