using System.Reflection;
using System.Text.Json;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Notifications;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Plugins.DataSpecialized;
using NexusPipeline.Modules.Plugins.Managed;
using NexusPipeline.Modules.Plugins.Repository;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Settings.Persistence;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Serialization;
using NexusPipeline.Shared.Common;

namespace NexusPipeline.Modules.Plugins.Runtime;

internal sealed partial class PluginManager : IPluginCapabilityResolver, IPluginAvailability, IUserRunStartingPublisher, IEmulatorSupportProviderResolver, IPluginExecutionProviderResolver, IDisposable
{
    private const int PluginApiMajor = PluginApiVersion.Major;
    private const int PluginApiMinor = PluginApiVersion.Minor;

    private readonly ISettingsProvider _settings;
    private readonly IPluginNotificationSink _notifications;
    private readonly IPluginConfigurationMutationGate _configurationGate;
    private readonly PluginDiscovery _discovery;
    private readonly List<DataSpecializedPlugin> _dataPlugins = new();
    private readonly List<ManagedPluginDescriptor> _managedPlugins = new();
    private readonly Dictionary<string, ManagedPluginRuntime> _managedRuntimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly PluginCapabilityRegistry _capabilities = new();
    private readonly Dictionary<string, bool> _configuredEnabled = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PluginRuntimeState> _runtimeStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _runtimeErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _runtimeErrorCodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<List<DataSpecializedPlugin>> _discoverData;
    private readonly OutboundHttpClientProvider _http;
    private readonly HostVersionInfo _hostVersion;
    private readonly PluginUserGlobalManagementRegistry _userGlobalManagement = new();
    private readonly PluginUserListBadgeRegistry _userListBadges = new();
    private readonly PluginExecutionEventRegistry _executionEvents;
    private readonly PluginUiContributionRegistry _uiContributions = new();
    private readonly PluginWebApiRegistry _webApi = new();
    private readonly PluginHistoryContributionRegistry _historyContributions = new();
    private readonly PluginEmulatorSupportRegistry _emulatorSupport = new();
    private readonly PluginExecutionProviderRegistry _executionProviders;
    private readonly PluginDashboardCardRegistry _dashboardCards = new();
    internal NexusPipeline.Modules.BrowserLogin.BrowserLoginService BrowserLogin { get; } = new();

    public void Dispose() => BrowserLogin.Dispose();

    internal PluginDashboardCardRegistry.Card[] DashboardCards => _dashboardCards.Snapshot().Where(card => IsRuntimeEnabled(card.Owner)).ToArray();
    internal long DashboardCardRevision => _dashboardCards.Revision;
    internal event Action? DashboardCardsChanged
    {
        add => _dashboardCards.Changed += value;
        remove => _dashboardCards.Changed -= value;
    }
    private readonly PluginManagementSnapshotCache _managementSnapshotCache = new();

    internal PluginManager(
        ISettingsProvider settings,
        IPluginNotificationSink notifications,
        IPluginConfigurationMutationGate configurationGate,
        Func<List<DataSpecializedPlugin>>? discoverData = null,
        OutboundHttpClientProvider? http = null,
        HostVersionInfo? hostVersion = null)
    {
        _settings = settings;
        _notifications = notifications;
        _configurationGate = configurationGate;
        _executionProviders = new(configurationGate);
        _discovery = new PluginDiscovery(_settings);
        _http = http ?? new OutboundHttpClientProvider(() => OutboundProxyOptions.Direct);
        _hostVersion = hostVersion ?? HostVersionInfo.Current;
        _executionEvents = new PluginExecutionEventRegistry((pluginName, exception) =>
        {
            _runtimeErrors[pluginName] = exception.Message;
            Logger.Warn($"[插件:{pluginName}] 用户运行事件处理失败：{exception.Message}");
        });
        _discoverData = discoverData ?? _discovery.DiscoverDataPlugins;
    }

    public void Publish(PluginUserRunStartingEvent eventData)
    {
        _executionEvents.Publish(eventData);
    }

    internal void DeleteUserData(string userId)
    {
        PluginUserDataStore.DeleteAllForUser(userId);
        PluginScopedDataStore.DeleteUserData(userId);
    }

    internal void DeleteUserScriptData(string userId, string scriptId)
    {
        PluginScopedDataStore.DeleteUserScriptData(userId, scriptId);
    }

    internal void DeleteScriptData(string scriptId)
    {
        PluginScopedDataStore.DeleteScriptData(scriptId);
    }

    internal void DeleteQueueData(string queueId)
    {
        PluginScopedDataStore.DeleteQueueData(queueId);
    }

    /// <summary>专项插件是否支持安卓模拟器启动方式，由插件 manifest capability 声明。</summary>
    public bool SupportsEmulator(string pluginName)
    {
        return HasCapability(pluginName, PluginCapabilityKeys.Emulator);
    }

    public bool HasCapability(string pluginName, string capabilityKey)
    {
        return _capabilities.HasKey(pluginName.Trim(), capabilityKey, IsRuntimeEnabled);
    }

    public IReadOnlyList<T> GetCapabilities<T>() where T : class, IPluginCapability
    {
        return _capabilities.GetAll<T>(IsRuntimeEnabled);
    }

    public ExecutionProviderDescriptor? ResolveExecutionProvider(string providerId)
    {
        IPluginExecutionProvider? provider = _executionProviders.Resolve(providerId, IsRuntimeEnabled);
        ManagedPluginDescriptor? owner = _managedPlugins.FirstOrDefault(item =>
            string.Equals(item.Manifest.Name, providerId, StringComparison.Ordinal));
        return provider is null || owner is null ? null
            : new ExecutionProviderDescriptor(providerId, owner.Manifest.Version, owner.Directory, provider);
    }

    public IReadOnlyList<EmulatorSupportProviderDescriptor> GetEmulatorSupportProviders() =>
        _emulatorSupport.Snapshot(IsRuntimeEnabled);

    internal IReadOnlyDictionary<string, PluginScriptTypeIcon?> GetScriptTypeIcons()
    {
        var sources = new Dictionary<string, PluginScriptTypeIcon?>(StringComparer.OrdinalIgnoreCase);
        foreach (DataSpecializedPlugin plugin in _dataPlugins.Where(plugin => IsRuntimeEnabled(plugin.Name)))
            sources.Add(plugin.Name, plugin.ScriptTypeIcon);
        foreach (ManagedPluginDescriptor plugin in _managedPlugins.Where(plugin => IsRuntimeEnabled(plugin.Manifest.Name)
            && _executionProviders.Resolve(plugin.Manifest.Name, IsRuntimeEnabled) is not null))
            sources.Add(plugin.Manifest.Name, plugin.Manifest.ScriptTypeIcon);
        return sources;
    }

    /// <summary>调用数据化专项插件按根目录推导配置快照；代码插件只有声明能力，不直接暴露宿主领域模型。</summary>
    public string ConfigurationRevision(string pluginName) =>
        (_capabilities.Get<IProfileResolver>(pluginName.Trim(), IsRuntimeEnabled) as DataSpecializedPlugin)?.ConfigurationRevision ?? "";

    public ScriptProfile? ResolveProfile(string pluginName, string rootPath, IReadOnlyDictionary<string, string>? inputs = null)
    {
        if (string.IsNullOrWhiteSpace(pluginName) || string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }
        pluginName = pluginName.Trim();
        IProfileResolver? resolver = _capabilities.Get<IProfileResolver>(pluginName, IsRuntimeEnabled);
        if (resolver is null)
        {
            return null;
        }
        try
        {
            return resolver.Resolve(rootPath.Trim(), inputs);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件] 插件「{pluginName}」解析「{rootPath}」失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>复用配置候选：数据化专项插件 configPath 模板引用单个输入且目标缺失时，枚举静态目录中可绑定的输入值。</summary>
    public IReadOnlyList<string> GetMissingConfigCandidates(string pluginName, string rootPath, IReadOnlyDictionary<string, string>? inputs)
    {
        return GetMissingConfigCandidateSet(pluginName, rootPath, inputs)?.Values
            ?? Array.Empty<string>();
    }

    public ConfigInputCandidateSet? GetMissingConfigCandidateSet(string pluginName, string rootPath, IReadOnlyDictionary<string, string>? inputs)
    {
        if (string.IsNullOrWhiteSpace(pluginName) || string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }
        pluginName = pluginName.Trim();
        IProfileResolver? resolver = _capabilities.Get<IProfileResolver>(pluginName, IsRuntimeEnabled);
        if (resolver is not DataSpecializedPlugin plugin
            || !plugin.TryDiscoverConfigInputCandidates(rootPath.Trim(), out ConfigInputCandidateSet? candidates))
        {
            return null;
        }
        return candidates;
    }

    public bool IsEnabled(string name)
    {
        return IsRuntimeEnabled(name.Trim());
    }

    /// <summary>配置开关状态；保存后运行态保持原状，下一次加载才应用。</summary>
    public bool IsConfiguredEnabled(string name)
    {
        string lookupName = name.Trim();
        return _configuredEnabled.TryGetValue(lookupName, out bool enabled)
            ? enabled
            : IsKnownPlugin(lookupName) && ReadConfiguredEnabled(lookupName, IsManagedCode(lookupName));
    }

    public string GetRuntimeState(string name)
    {
        string lookupName = name.Trim();
        return _runtimeStates.TryGetValue(lookupName, out PluginRuntimeState state)
            ? state.ToString()
            : PluginRuntimeState.Discovered.ToString();
    }

    public string? GetRuntimeError(string name)
    {
        return _runtimeErrors.TryGetValue(name.Trim(), out string? error) ? error : null;
    }

    internal string? GetRuntimeErrorCode(string name)
    {
        string lookupName = name.Trim();
        return _runtimeErrors.ContainsKey(lookupName)
            ? _runtimeErrorCodes.GetValueOrDefault(lookupName, "plugin_runtime_error")
            : null;
    }

    public bool IsKnownPlugin(string name)
    {
        string lookupName = name.Trim();
        return HasActualPluginName(lookupName);
    }

    public bool IsDataSpecializedPlugin(string name)
    {
        string lookupName = name.Trim();
        return _dataPlugins.Any(plugin => string.Equals(plugin.Name, lookupName, StringComparison.OrdinalIgnoreCase));
    }

    public bool SetEnabled(string name, bool enabled, string source = Audit.System)
    {
        return SetEnabled(name, enabled, source, out _);
    }

    public bool SetEnabled(string name, bool enabled, string source, out string? failureCode)
    {
        string lookupName = name.Trim();
        if (!IsKnownPlugin(lookupName))
        {
            Logger.Warn($"[插件] 插件「{name}」不存在，已忽略启用开关操作。");
            failureCode = "not_found";
            return false;
        }
        bool changed = _configurationGate.TryExecute(() =>
        {
            AppSettings settings = _settings.Current;
            settings.PluginPreferences ??= new Dictionary<string, PluginPreference>(StringComparer.OrdinalIgnoreCase);
            string key = settings.PluginPreferences.Keys.FirstOrDefault(item => string.Equals(item, lookupName, StringComparison.OrdinalIgnoreCase)) ?? lookupName;
            settings.PluginPreferences[key] = new PluginPreference
            {
                Enabled = enabled,
            };
            AppSettingsStore.Save(settings);
        }, out string? gateFailureCode);
        if (!changed)
        {
            failureCode = gateFailureCode ?? "host_maintenance";
            return false;
        }
        _configuredEnabled[lookupName] = enabled;
        InvalidateManagementSnapshot();
        Audit.Log(source, $"{(enabled ? "启用" : "禁用")}插件", lookupName);
        Logger.Info($"[插件] 已{(enabled ? "启用" : "禁用")}：{lookupName}（重启后生效）。");
        failureCode = null;
        return true;
    }

    internal bool HasFrontend(string name)
    {
        name = name.Trim();
        return _managedPlugins.Any(plugin => string.Equals(plugin.Manifest.Name, name, StringComparison.OrdinalIgnoreCase) && plugin.Manifest.Frontend is not null);
    }

    internal bool TryGetPluginDirectory(string name, out string? directory)
    {
        name = name.Trim();
        directory = _dataPlugins
            .FirstOrDefault(plugin => string.Equals(plugin.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.PluginDirectory;
        if (directory is not null)
        {
            return true;
        }
        directory = _managedPlugins
            .FirstOrDefault(plugin => string.Equals(plugin.Manifest.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.Directory;
        return directory is not null;
    }

    private bool HasActualPluginName(string name)
    {
        return _dataPlugins.Any(plugin => string.Equals(plugin.Name, name, StringComparison.OrdinalIgnoreCase))
            || _managedPlugins.Any(plugin => string.Equals(plugin.Manifest.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsRuntimeEnabled(string name)
    {
        return _runtimeStates.TryGetValue(name, out PluginRuntimeState state)
            && state == PluginRuntimeState.Active;
    }

    private bool IsManagedCode(string name)
    {
        return _managedPlugins.Any(plugin => string.Equals(plugin.Manifest.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 扫描 manifest 后按配置启动插件。managed-code 插件在完成 API 兼容性和启用检查前不会加载程序集。
    /// </summary>
    public void LoadAll()
    {
        InvalidateManagementSnapshot();
        if (_dataPlugins.Count > 0 || _managedPlugins.Count > 0 || _managedRuntimes.Count > 0)
        {
            ShutdownAll();
        }
        _userGlobalManagement.Clear();
        _userListBadges.Clear();
        _executionEvents.Clear();
        _uiContributions.Clear();
        _webApi.Clear();
        _historyContributions.Clear();
        _emulatorSupport.Clear();
        _executionProviders.Clear();
        _dashboardCards.Clear();
        _dataPlugins.Clear();
        _managedPlugins.Clear();
        _managedRuntimes.Clear();
        _capabilities.Clear();
        _configuredEnabled.Clear();
        _runtimeStates.Clear();
        _runtimeErrors.Clear();
        _runtimeErrorCodes.Clear();

        foreach (DataSpecializedPlugin plugin in _discoverData())
        {
            AddDataPlugin(plugin);
        }
        DiscoverManagedPlugins();

        foreach (DataSpecializedPlugin plugin in _dataPlugins)
        {
            bool enabled = ReadConfiguredEnabled(plugin.Name, managedCode: false);
            _configuredEnabled[plugin.Name] = enabled;
            if (!PluginRepositoryCatalog.IsHostVersionCompatible(
                    plugin.MinHostVersion,
                    _hostVersion.CurrentVersion,
                    out string hostCompatibilityReason))
            {
                _runtimeStates[plugin.Name] = PluginRuntimeState.Incompatible;
                _runtimeErrors[plugin.Name] = hostCompatibilityReason;
                _runtimeErrorCodes[plugin.Name] = "plugin_incompatible_host";
                Logger.Warn($"[插件] 插件「{plugin.DisplayName}」需要更高宿主版本，运行时未启用：{hostCompatibilityReason}");
                continue;
            }
            _capabilities.Register(plugin.Name, plugin);
            _capabilities.RegisterKeys(plugin.Name, plugin.CapabilityKeys);
            _runtimeStates[plugin.Name] = enabled ? PluginRuntimeState.Active : PluginRuntimeState.Disabled;
            Logger.Info($"[插件] 已{(enabled ? "启用" : "禁用")}：{plugin.DisplayName} v{plugin.Version}（数据化专项）");
        }
        foreach (ManagedPluginDescriptor descriptor in _managedPlugins)
        {
            string name = descriptor.Manifest.Name;
            bool enabled = ReadConfiguredEnabled(name, managedCode: true);
            _configuredEnabled[name] = enabled;
            if (!PluginRepositoryCatalog.IsHostVersionCompatible(
                    descriptor.Manifest.MinHostVersion,
                    _hostVersion.CurrentVersion,
                    out string hostCompatibilityReason))
            {
                _runtimeStates[name] = PluginRuntimeState.Incompatible;
                _runtimeErrors[name] = hostCompatibilityReason;
                _runtimeErrorCodes[name] = "plugin_incompatible_host";
                Logger.Warn($"[插件] 插件「{descriptor.Manifest.DisplayName}」需要更高宿主版本，程序集未加载：{hostCompatibilityReason}");
                continue;
            }
            if (!descriptor.Manifest.IsCompatibleWith(PluginApiMajor, PluginApiMinor))
            {
                _runtimeStates[name] = PluginRuntimeState.Incompatible;
                _runtimeErrors[name] = $"不支持 Plugin API v{descriptor.Manifest.ApiVersion}（宿主要求 v{PluginApiMajor}.{PluginApiMinor}）";
                _runtimeErrorCodes[name] = "plugin_incompatible_api";
                Logger.Warn($"[插件] 插件「{descriptor.Manifest.DisplayName}」与 Plugin API 不兼容，程序集未加载。");
                continue;
            }
            try { ManagedPluginContract.Validate(descriptor.Directory, descriptor.Manifest); }
            catch (Exception ex)
            {
                _runtimeStates[name] = PluginRuntimeState.Incompatible;
                _runtimeErrors[name] = ex.Message;
                _runtimeErrorCodes[name] = "plugin_incompatible_assembly";
                continue;
            }
            _capabilities.RegisterKeys(name, descriptor.Manifest.Capabilities);
            if (!enabled)
            {
                _runtimeStates[name] = PluginRuntimeState.Disabled;
                Logger.Info($"[插件] 已禁用：{descriptor.Manifest.DisplayName}（managed-code，程序集未加载）");
                continue;
            }
            StartManagedPlugin(descriptor);
        }
    }

    public void ShutdownAll()
    {
        foreach ((string name, ManagedPluginRuntime runtime) in _managedRuntimes.ToArray())
        {
            try
            {
                runtime.Stop();
            }
            catch (Exception ex)
            {
                Logger.Warn($"插件「{name}」关停失败：{ex.Message}");
            }
            finally
            {
                _runtimeStates[name] = runtime.StopTimedOut
                    ? PluginRuntimeState.StopTimedOut
                    : PluginRuntimeState.Shutdown;
            }
        }
        _managedRuntimes.Clear();
        _userGlobalManagement.Clear();
        _userListBadges.Clear();
        _executionEvents.Clear();
        _uiContributions.Clear();
        _webApi.Clear();
        _historyContributions.Clear();
        _emulatorSupport.Clear();
        _executionProviders.Clear();
        _dashboardCards.Clear();
        foreach (DataSpecializedPlugin plugin in _dataPlugins)
        {
            _runtimeStates[plugin.Name] = PluginRuntimeState.Shutdown;
        }
        InvalidateManagementSnapshot();
    }

    private void AddDataPlugin(DataSpecializedPlugin plugin)
    {
        if (IsKnownPlugin(plugin.Name))
        {
            Logger.Warn($"[插件] 检测到重复插件名「{plugin.Name}」，跳过数据化插件。");
            return;
        }
        _dataPlugins.Add(plugin);
        _runtimeStates[plugin.Name] = PluginRuntimeState.Discovered;
    }

    private void DiscoverManagedPlugins()
    {
        IReadOnlySet<string> knownNames = _dataPlugins
            .Select(plugin => plugin.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (ManagedPluginDescriptor descriptor in _discovery.DiscoverManagedPlugins(knownNames))
        {
            _managedPlugins.Add(descriptor);
            _runtimeStates[descriptor.Manifest.Name] = PluginRuntimeState.Discovered;
        }
    }

    private bool ReadConfiguredEnabled(string name, bool managedCode) =>
        _discovery.ReadConfiguredEnabled(name, managedCode);

    private void StartManagedPlugin(ManagedPluginDescriptor descriptor)
    {
        string name = descriptor.Manifest.Name;
        _runtimeStates[name] = PluginRuntimeState.Loading;
        try
        {
            var runtime = new ManagedPluginRuntime(
                descriptor,
                _notifications,
                _userGlobalManagement,
                _userListBadges,
                _executionEvents,
                _http,
                _uiContributions,
                _webApi,
                _historyContributions,
                _emulatorSupport,
                _executionProviders,
                _dashboardCards,
                BrowserLogin,
                ex =>
                {
                    _runtimeErrors[name] = ex.Message;
                    Logger.Warn($"[插件:{name}] 后台任务失败：{ex.Message}");
                });
            runtime.Start();
            _managedRuntimes[name] = runtime;
            _runtimeStates[name] = PluginRuntimeState.Active;
            _dashboardCards.AvailabilityChanged();
            Logger.Info($"[插件] 已启用：{descriptor.Manifest.DisplayName} v{descriptor.Manifest.Version}（managed-code）");
        }
        catch (PluginLifecycleTimeoutException ex)
        {
            _runtimeStates[name] = ex.Phase == PluginLifecyclePhase.Initialize
                ? PluginRuntimeState.InitTimedOut
                : PluginRuntimeState.StartTimedOut;
            _runtimeErrors[name] = ex.Message;
            Logger.Warn($"[插件] 插件「{descriptor.Manifest.DisplayName}」生命周期超时：{ex.Message}");
        }
        catch (Exception ex)
        {
            _runtimeStates[name] = PluginRuntimeState.InitFailed;
            _runtimeErrors[name] = ex.Message;
            Logger.Warn($"[插件] 插件「{descriptor.Manifest.DisplayName}」初始化失败：{ex.Message}");
        }
    }

    internal IReadOnlyList<PluginUserGlobalManagementRegistration> UserGlobalManagementContributions =>
        _userGlobalManagement.Snapshot();

    internal PluginLocalizationManifest GetPluginLocalization(string pluginName)
    {
        DataSpecializedPlugin? data = _dataPlugins.FirstOrDefault(plugin =>
            string.Equals(plugin.Name, pluginName, StringComparison.OrdinalIgnoreCase));
        if (data is not null)
        {
            return data.Localization;
        }
        ManagedPluginDescriptor? managed = _managedPlugins.FirstOrDefault(plugin =>
            string.Equals(plugin.Manifest.Name, pluginName, StringComparison.OrdinalIgnoreCase));
        return managed?.Manifest.Localization ?? PluginLocalizationManifest.Empty;
    }

    internal IReadOnlyList<PluginUserListBadgeRegistration> UserListBadgeContributions =>
        _userListBadges.Snapshot();

    internal IReadOnlyList<PluginUiContributionRegistration> UiContributions =>
        _uiContributions.Snapshot();

    internal bool TryGetUiContribution(
        string pluginName,
        string contributionId,
        out PluginUiContributionRegistration? registration) =>
        _uiContributions.TryGet(pluginName, contributionId, out registration);


    internal bool TryGetWebApi(
        string pluginName,
        string method,
        string route,
        out PluginWebApiRegistration? registration) =>
        _webApi.TryGet(pluginName, method, route, out registration);

    internal IReadOnlyList<PluginHistoryContributionRegistration> HistoryContributions =>
        _historyContributions.Snapshot();

}
