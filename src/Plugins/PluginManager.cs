using System.Reflection;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Extensibility;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Plugins.Managed;
using NexusPipeline.Services;
using NexusPipeline.Services.Networking;
using NexusPipeline.Services.Notification;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

/// <summary>插件统一元数据投影。仅包含真实数据插件和 managed-code 插件。</summary>
internal sealed record PluginSummary(
    string Name,
    string DisplayName,
    string GameName,
    string Description,
    string Version,
    string Kind,
    string ApiVersion,
    IReadOnlyList<string> Capabilities);

internal enum PluginRuntimeState
{
    Discovered,
    Disabled,
    Incompatible,
    Loading,
    Active,
    InitFailed,
    Shutdown,
}

internal sealed class PluginManager : IPluginCapabilityResolver, IPluginAvailability, IUserRunStartingPublisher
{
    private const int PluginApiMajor = PluginApiVersion.Major;
    private const int PluginApiMinor = PluginApiVersion.Minor;

    private readonly Func<AppSettings> _settings;
    private readonly Func<NotificationDispatcher> _notifications;

    private readonly Func<Action, bool> _tryConfigurationMutation;
    private readonly List<DataSpecializedPlugin> _dataPlugins = new();
    private readonly List<ManagedPluginDescriptor> _managedPlugins = new();
    private readonly Dictionary<string, ManagedPluginRuntime> _managedRuntimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly PluginCapabilityRegistry _capabilities = new();
    private readonly Dictionary<string, bool> _configuredEnabled = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PluginRuntimeState> _runtimeStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _runtimeErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<List<DataSpecializedPlugin>> _discoverData;
    private readonly OutboundHttpClientProvider _http;
    private readonly PluginUserGlobalManagementRegistry _userGlobalManagement = new();
    private readonly PluginUserListBadgeRegistry _userListBadges = new();
    private readonly PluginExecutionEventRegistry _executionEvents;

    internal PluginManager(
        Func<AppSettings> settings,
        Func<NotificationDispatcher> notifications,
        Func<List<DataSpecializedPlugin>>? discoverData = null,
        Func<Action, bool>? tryConfigurationMutation = null,
        OutboundHttpClientProvider? http = null)
    {
        _settings = settings;
        _notifications = notifications;
        _http = http ?? new OutboundHttpClientProvider(settings);
        _executionEvents = new PluginExecutionEventRegistry((pluginName, exception) =>
        {
            _runtimeErrors[pluginName] = exception.Message;
            Logger.Warn($"[插件:{pluginName}] 用户运行事件处理失败：{exception.Message}");
        });
        _discoverData = discoverData ?? DiscoverDataPlugins;
        _tryConfigurationMutation = tryConfigurationMutation ?? (mutation =>
        {
            mutation();
            return true;
        });
    }

    /// <summary>插件统一元数据投影（专项数据插件 + managed-code 代码插件）。</summary>
    public IReadOnlyList<PluginSummary> PluginSummaries
    {
        get
        {
            var list = new List<PluginSummary>();
            foreach (DataSpecializedPlugin plugin in _dataPlugins)
            {
                list.Add(new PluginSummary(
                    plugin.Name,
                    plugin.DisplayName,
                    plugin.GameName,
                    plugin.Description,
                    plugin.Version,
                    "data-specialized",
                    "",
                    plugin.CapabilityKeys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray()));
            }
            foreach (ManagedPluginDescriptor plugin in _managedPlugins)
            {
                list.Add(new PluginSummary(
                    plugin.Manifest.Name,
                    plugin.Manifest.DisplayName,
                    "",
                    plugin.Manifest.Description,
                    plugin.Manifest.Version,
                    "managed-code",
                    plugin.Manifest.ApiVersion,
                    plugin.Manifest.Capabilities.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray()));
            }
            return list;
        }
    }

    internal IReadOnlyList<PluginUserGlobalManagementRegistration> UserGlobalManagementContributions =>
        _userGlobalManagement.Snapshot();

    internal bool TryGetUserGlobalManagementContribution(
        string pluginName,
        string contributionId,
        out PluginUserGlobalManagementRegistration? registration) =>
        _userGlobalManagement.TryGet(pluginName, contributionId, out registration);

    internal IReadOnlyList<PluginUserListBadgeRegistration> UserListBadgeContributions =>
        _userListBadges.Snapshot();

    public void Publish(PluginUserRunStartingEvent eventData)
    {
        _executionEvents.Publish(eventData);
    }

    internal void DeleteUserData(string userId)
    {
        PluginUserDataStore.DeleteAllForUser(userId);
    }

    /// <summary>专项插件是否支持安卓模拟器启动方式，由插件 manifest capability 声明。</summary>
    public bool SupportsEmulator(string pluginName)
    {
        return HasCapability(pluginName, PluginCapabilityKeys.Emulator);
    }

    public bool HasCapability(string pluginName, string capabilityKey)
    {
        return _capabilities.HasKey(pluginName, capabilityKey, IsRuntimeEnabled);
    }

    public IReadOnlyList<T> GetCapabilities<T>() where T : class, IPluginCapability
    {
        return _capabilities.GetAll<T>(IsRuntimeEnabled);
    }

    /// <summary>调用数据化专项插件按根目录推导配置快照；代码插件只有声明能力，不直接暴露宿主领域模型。</summary>
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

    /// <summary>
    /// 扫描 manifest 后按配置启动插件。managed-code 插件在完成 API 兼容性和启用检查前不会加载程序集。
    /// </summary>
    public void LoadAll()
    {
        if (_dataPlugins.Count > 0 || _managedPlugins.Count > 0 || _managedRuntimes.Count > 0)
        {
            ShutdownAll();
        }
        _userGlobalManagement.Clear();
        _userListBadges.Clear();
        _executionEvents.Clear();
        _dataPlugins.Clear();
        _managedPlugins.Clear();
        _managedRuntimes.Clear();
        _capabilities.Clear();
        _configuredEnabled.Clear();
        _runtimeStates.Clear();
        _runtimeErrors.Clear();

        foreach (DataSpecializedPlugin plugin in _discoverData())
        {
            AddDataPlugin(plugin);
        }
        DiscoverManagedPlugins();

        foreach (DataSpecializedPlugin plugin in _dataPlugins)
        {
            bool enabled = ReadConfiguredEnabled(plugin.Name, managedCode: false);
            _configuredEnabled[plugin.Name] = enabled;
            _runtimeStates[plugin.Name] = enabled ? PluginRuntimeState.Active : PluginRuntimeState.Disabled;
            Logger.Info($"[插件] 已{(enabled ? "启用" : "禁用")}：{plugin.DisplayName} v{plugin.Version}（数据化专项）");
        }
        foreach (ManagedPluginDescriptor descriptor in _managedPlugins)
        {
            string name = descriptor.Manifest.Name;
            bool enabled = ReadConfiguredEnabled(name, managedCode: true);
            _configuredEnabled[name] = enabled;
            if (!enabled)
            {
                _runtimeStates[name] = PluginRuntimeState.Disabled;
                Logger.Info($"[插件] 已禁用：{descriptor.Manifest.DisplayName}（managed-code，程序集未加载）");
                continue;
            }
            if (!descriptor.Manifest.IsCompatibleWith(PluginApiMajor, PluginApiMinor))
            {
                _runtimeStates[name] = PluginRuntimeState.Incompatible;
                _runtimeErrors[name] = $"不支持 Plugin API v{descriptor.Manifest.ApiVersion}（宿主支持 v{PluginApiMajor}.{PluginApiMinor} 及兼容的更低 minor）";
                Logger.Warn($"[插件] 插件「{descriptor.Manifest.DisplayName}」与 Plugin API 不兼容，程序集未加载。");
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
                _runtimeStates[name] = PluginRuntimeState.Shutdown;
            }
        }
        _managedRuntimes.Clear();
        _userGlobalManagement.Clear();
        _userListBadges.Clear();
        _executionEvents.Clear();
        foreach (DataSpecializedPlugin plugin in _dataPlugins)
        {
            _runtimeStates[plugin.Name] = PluginRuntimeState.Shutdown;
        }
    }

    public bool IsEnabled(string name)
    {
        return IsRuntimeEnabled(name);
    }

    /// <summary>配置开关状态；保存后运行态保持原状，下一次加载才应用。</summary>
    public bool IsConfiguredEnabled(string name)
    {
        return _configuredEnabled.TryGetValue(name, out bool enabled)
            ? enabled
            : IsKnownPlugin(name) && ReadConfiguredEnabled(name, IsManagedCode(name));
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
        return _dataPlugins.Any(plugin => string.Equals(plugin.Name, name, StringComparison.OrdinalIgnoreCase))
            || _managedPlugins.Any(plugin => string.Equals(plugin.Manifest.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsDataSpecializedPlugin(string name)
    {
        return _dataPlugins.Any(plugin => string.Equals(plugin.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public bool SetEnabled(string name, bool enabled, string source = Audit.System)
    {
        return SetEnabled(name, enabled, source, out _);
    }

    public bool SetEnabled(string name, bool enabled, string source, out string? failureCode)
    {
        if (!IsKnownPlugin(name))
        {
            Logger.Warn($"[插件] 插件「{name}」不存在，已忽略启用开关操作。");
            failureCode = "not_found";
            return false;
        }
        bool changed = _tryConfigurationMutation(() =>
        {
            lock (RuntimeContext.Instance.SettingsMutationLock)
            {
                AppSettings settings = _settings();
                settings.PluginPreferences ??= new Dictionary<string, PluginPreference>(StringComparer.OrdinalIgnoreCase);
                string key = settings.PluginPreferences.Keys.FirstOrDefault(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase)) ?? name;
                settings.PluginPreferences[key] = new PluginPreference { Enabled = enabled };
                ConfigStore.Save(settings);
            }
        });
        if (!changed)
        {
            failureCode = "host_maintenance";
            return false;
        }
        _configuredEnabled[name] = enabled;
        Audit.Log(source, $"{(enabled ? "启用" : "禁用")}插件", name);
        Logger.Info($"[插件] 已{(enabled ? "启用" : "禁用")}：{name}（重启后生效）。");
        failureCode = null;
        return true;
    }

    private void AddDataPlugin(DataSpecializedPlugin plugin)
    {
        if (IsKnownPlugin(plugin.Name))
        {
            Logger.Warn($"[插件] 检测到重复插件名「{plugin.Name}」，跳过数据化插件。");
            return;
        }
        _dataPlugins.Add(plugin);
        _capabilities.Register(plugin.Name, plugin);
        _capabilities.RegisterKeys(plugin.Name, plugin.CapabilityKeys);
        _runtimeStates[plugin.Name] = PluginRuntimeState.Discovered;
    }

    private void DiscoverManagedPlugins()
    {
        if (!Directory.Exists(AppPaths.PluginsDir))
        {
            return;
        }
        foreach (string directory in Directory.GetDirectories(AppPaths.PluginsDir))
        {
            if (!PluginManifest.TryLoad(directory, out PluginManifest? manifest, out string? error) || manifest is null)
            {
                Logger.Warn($"[插件] 跳过无效插件目录：{Path.GetFileName(directory)}（{error}）");
                continue;
            }
            if (manifest.Kind != "managed-code")
            {
                continue;
            }
            if (IsKnownPlugin(manifest.Name))
            {
                Logger.Warn($"[插件] 检测到重复插件名「{manifest.Name}」，跳过 managed-code 插件。");
                continue;
            }
            var descriptor = new ManagedPluginDescriptor(manifest, directory);
            _managedPlugins.Add(descriptor);
            _capabilities.RegisterKeys(manifest.Name, manifest.Capabilities);
            _runtimeStates[manifest.Name] = PluginRuntimeState.Discovered;
        }
    }

    private void StartManagedPlugin(ManagedPluginDescriptor descriptor)
    {
        string name = descriptor.Manifest.Name;
        _runtimeStates[name] = PluginRuntimeState.Loading;
        try
        {
            var runtime = new ManagedPluginRuntime(
                descriptor,
                _notifications(),
                _userGlobalManagement,
                _userListBadges,
                _executionEvents,
                _http,
                ex =>
                {
                    _runtimeErrors[name] = ex.Message;
                    Logger.Warn($"[插件:{name}] 后台任务失败：{ex.Message}");
                });
            runtime.Start();
            _managedRuntimes[name] = runtime;
            _runtimeStates[name] = PluginRuntimeState.Active;
            Logger.Info($"[插件] 已启用：{descriptor.Manifest.DisplayName} v{descriptor.Manifest.Version}（managed-code）");
        }
        catch (Exception ex)
        {
            _runtimeStates[name] = PluginRuntimeState.InitFailed;
            _runtimeErrors[name] = ex.Message;
            Logger.Warn($"[插件] 插件「{descriptor.Manifest.DisplayName}」初始化失败：{ex.Message}");
        }
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

    private bool ReadConfiguredEnabled(string name, bool managedCode)
    {
        AppSettings settings = _settings();
        PluginPreference? preference = settings.PluginPreferences?
            .FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        return preference?.Enabled ?? !managedCode;
    }

    private static List<DataSpecializedPlugin> DiscoverDataPlugins()
    {
        var list = new List<DataSpecializedPlugin>();
        if (!Directory.Exists(AppPaths.PluginsDir))
        {
            return list;
        }
        foreach (string directory in Directory.GetDirectories(AppPaths.PluginsDir))
        {
            if (!PluginManifest.TryLoad(directory, out PluginManifest? manifest, out _)
                || manifest is null
                || manifest.Kind != "data-specialized")
            {
                continue;
            }
            DataSpecializedPlugin? plugin = DataSpecializedPlugin.Load(directory);
            if (plugin is not null)
            {
                list.Add(plugin);
            }
            else
            {
                Logger.Warn($"[插件] 跳过无效数据化插件目录：{Path.GetFileName(directory)}");
            }
        }
        return list;
    }

    private sealed record ManagedPluginDescriptor(PluginManifest Manifest, string Directory);

    private sealed class ManagedPluginRuntime
    {
        private readonly ManagedPluginDescriptor _descriptor;
        private readonly NotificationDispatcher _notifications;
        private readonly PluginUserGlobalManagementRegistry _userGlobalManagement;
        private readonly PluginUserListBadgeRegistry _userListBadges;
        private readonly PluginExecutionEventRegistry _executionEvents;
        private readonly OutboundHttpClientProvider _http;
        private readonly Action<Exception> _reportJobError;
        private PluginLoadContext? _loadContext;
        private INexusPlugin? _plugin;
        private PluginHostContext? _hostContext;

        public ManagedPluginRuntime(
            ManagedPluginDescriptor descriptor,
            NotificationDispatcher notifications,
            PluginUserGlobalManagementRegistry userGlobalManagement,
            PluginUserListBadgeRegistry userListBadges,
            PluginExecutionEventRegistry executionEvents,
            OutboundHttpClientProvider http,
            Action<Exception> reportJobError)
        {
            _descriptor = descriptor;
            _notifications = notifications;
            _userGlobalManagement = userGlobalManagement;
            _userListBadges = userListBadges;
            _executionEvents = executionEvents;
            _http = http;
            _reportJobError = reportJobError;
        }

        public void Start()
        {
            try
            {
                string pluginDirectory = Path.GetFullPath(_descriptor.Directory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                string entryPath = Path.GetFullPath(Path.Combine(pluginDirectory, _descriptor.Manifest.EntryAssembly));
                if (!entryPath.StartsWith(pluginDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("插件 entryAssembly 必须位于插件目录内");
                }
                if (!File.Exists(entryPath))
                {
                    throw new FileNotFoundException("找不到插件 entryAssembly", entryPath);
                }
                _loadContext = new PluginLoadContext(entryPath);
                Assembly assembly = _loadContext.LoadEntryAssembly(entryPath);
                Type type = assembly.GetType(_descriptor.Manifest.EntryType, throwOnError: true, ignoreCase: false)
                    ?? throw new InvalidOperationException($"找不到插件 entryType：{_descriptor.Manifest.EntryType}");
                if (Activator.CreateInstance(type) is not INexusPlugin plugin)
                {
                    throw new InvalidOperationException($"插件类型未实现 INexusPlugin：{_descriptor.Manifest.EntryType}");
                }
                _plugin = plugin;
                _hostContext = new PluginHostContext(
                    _descriptor.Manifest.Name,
                    _descriptor.Manifest.DisplayName,
                    _notifications,
                    _reportJobError,
                    _userGlobalManagement,
                    _userListBadges,
                    _executionEvents,
                    _http);
                plugin.InitializeAsync(_hostContext, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                plugin.StartAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                Cleanup();
                throw;
            }
        }

        public void Stop()
        {
            try
            {
                _plugin?.StopAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                Cleanup();
            }
        }

        private void Cleanup()
        {
            _hostContext?.Dispose();
            _plugin = null;
            _hostContext = null;
            _loadContext?.Unload();
            _loadContext = null;
        }
    }
}
