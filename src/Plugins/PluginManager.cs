using System.Reflection;
using System.Text.Json;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Extensibility;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Plugins.Managed;
using NexusPipeline.Services;
using NexusPipeline.Services.Networking;
using NexusPipeline.Services.Notification;
using NexusPipeline.Services.Update;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

/// <summary>插件统一元数据投影。仅包含真实数据插件和 managed-code 插件。</summary>
internal sealed record PluginSummary(
    string Name,
    string ArtifactName,
    string DisplayName,
    string GameName,
    string Description,
    string Version,
    string Kind,
    string ApiVersion,
    IReadOnlyList<string> Capabilities,
    bool HasFrontend,
    string FrontendApiVersion)
{
    public IReadOnlyList<PluginAuthor> Authors { get; init; } = Array.Empty<PluginAuthor>();

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public string Homepage { get; init; } = "";

    public string CreatedAt { get; init; } = "";

    public string UpdatedAt { get; init; } = "";

    public IReadOnlyList<PluginChangelogEntry> Changelog { get; init; } = Array.Empty<PluginChangelogEntry>();

    public IReadOnlyDictionary<string, PluginLocalizedMetadata> Locales { get; init; } =
        new Dictionary<string, PluginLocalizedMetadata>(StringComparer.OrdinalIgnoreCase);

    public bool HasReadme { get; init; }

    /// <summary>数据化专项插件 resolve.json 声明的用户输入变量（managed-code 恒为空）。</summary>
    public IReadOnlyList<PluginInputDeclaration> Inputs { get; init; } = Array.Empty<PluginInputDeclaration>();

    public string MinHostVersion { get; init; } = "0.0.0";
}

internal sealed record PluginFrontendRuntimeDescriptor(
    string Name,
    string DisplayName,
    string Version,
    string FrontendApiVersion,
    string EntryUrl,
    IReadOnlyList<string> StyleUrls,
    string DefaultLocale,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Localization);

internal enum PluginRuntimeState
{
    Discovered,
    Disabled,
    Incompatible,
    Loading,
    Active,
    InitFailed,
    InitTimedOut,
    StartTimedOut,
    StopTimedOut,
    Shutdown,
}

internal sealed partial class PluginManager : IPluginCapabilityResolver, IPluginAvailability, IUserRunStartingPublisher
{
    private const int PluginApiMajor = PluginApiVersion.Major;
    private const int PluginApiMinor = PluginApiVersion.Minor;

    private readonly Func<AppSettings> _settings;
    private readonly Func<NotificationDispatcher> _notifications;

    private readonly Func<Action, bool> _tryConfigurationMutation;
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
    private readonly PluginUserGlobalManagementRegistry _userGlobalManagement = new();
    private readonly PluginUserListBadgeRegistry _userListBadges = new();
    private readonly PluginExecutionEventRegistry _executionEvents;
    private readonly PluginUiContributionRegistry _uiContributions = new();
    private readonly PluginWebApiRegistry _webApi = new();
    private readonly PluginHistoryContributionRegistry _historyContributions = new();
    private readonly PluginManagementSnapshotCache _managementSnapshotCache = new();

    internal PluginManager(
        Func<AppSettings> settings,
        Func<NotificationDispatcher> notifications,
        Func<List<DataSpecializedPlugin>>? discoverData = null,
        Func<Action, bool>? tryConfigurationMutation = null,
        OutboundHttpClientProvider? http = null)
    {
        _settings = settings;
        _notifications = notifications;
        _discovery = new PluginDiscovery(_settings);
        _http = http ?? new OutboundHttpClientProvider(settings);
        _executionEvents = new PluginExecutionEventRegistry((pluginName, exception) =>
        {
            _runtimeErrors[pluginName] = exception.Message;
            Logger.Warn($"[插件:{pluginName}] 用户运行事件处理失败：{exception.Message}");
        });
        _discoverData = discoverData ?? _discovery.DiscoverDataPlugins;
        _tryConfigurationMutation = tryConfigurationMutation ?? (mutation =>
        {
            mutation();
            return true;
        });
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
        return _capabilities.HasKey(ResolveLoadedPluginName(pluginName), capabilityKey, IsRuntimeEnabled);
    }

    public IReadOnlyList<T> GetCapabilities<T>() where T : class, IPluginCapability
    {
        return _capabilities.GetAll<T>(IsRuntimeEnabled);
    }

    /// <summary>调用数据化专项插件按根目录推导配置快照；代码插件只有声明能力，不直接暴露宿主领域模型。</summary>
    public ScriptProfile? ResolveProfile(string pluginName, string rootPath, IReadOnlyDictionary<string, string>? inputs = null)
    {
        if (string.IsNullOrWhiteSpace(pluginName) || string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }
        pluginName = ResolveLoadedPluginName(pluginName);
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
        pluginName = ResolveLoadedPluginName(pluginName);
        IProfileResolver? resolver = _capabilities.Get<IProfileResolver>(pluginName, IsRuntimeEnabled);
        if (resolver is not DataSpecializedPlugin plugin
            || !plugin.TryDiscoverConfigInputCandidates(rootPath.Trim(), out ConfigInputCandidateSet? candidates))
        {
            return null;
        }
        return candidates;
    }

    /// <summary>返回已发现、已启用且有效的数据化插件配置校验脚本；普通脚本和 managed-code 插件不参与。</summary>
    internal bool TryGetConfigValidator(string pluginName, out ConfigValidatorDescriptor? descriptor)
    {
        descriptor = null;
        pluginName = ResolveLoadedPluginName(pluginName);
        DataSpecializedPlugin? plugin = _dataPlugins.FirstOrDefault(item =>
            string.Equals(item.Name, pluginName, StringComparison.OrdinalIgnoreCase));
        if (plugin is null || !IsRuntimeEnabled(plugin.Name) || !plugin.HasConfigValidator)
        {
            return false;
        }
        descriptor = plugin.ReadConfigValidator();
        return descriptor is not null;
    }

    /// <summary>返回已发现、已启用且有效的数据化插件配置编辑脚本。</summary>
    internal bool TryGetConfigEditor(string pluginName, out ConfigEditorDescriptor? descriptor)
    {
        descriptor = null;
        pluginName = ResolveLoadedPluginName(pluginName);
        DataSpecializedPlugin? plugin = _dataPlugins.FirstOrDefault(item =>
            string.Equals(item.Name, pluginName, StringComparison.OrdinalIgnoreCase));
        if (plugin is null || !IsRuntimeEnabled(plugin.Name) || !plugin.HasConfigEditor)
        {
            return false;
        }
        descriptor = plugin.ReadConfigEditor();
        return descriptor is not null;
    }

    public bool IsEnabled(string name)
    {
        return IsRuntimeEnabled(ResolveLoadedPluginName(name));
    }

    /// <summary>配置开关状态；保存后运行态保持原状，下一次加载才应用。</summary>
    public bool IsConfiguredEnabled(string name)
    {
        string lookupName = ResolveLoadedPluginName(name);
        return _configuredEnabled.TryGetValue(lookupName, out bool enabled)
            ? enabled
            : IsKnownPlugin(lookupName) && ReadConfiguredEnabled(lookupName, IsManagedCode(lookupName));
    }

    public string GetRuntimeState(string name)
    {
        string lookupName = ResolveLoadedPluginName(name);
        return _runtimeStates.TryGetValue(lookupName, out PluginRuntimeState state)
            ? state.ToString()
            : PluginRuntimeState.Discovered.ToString();
    }

    public string? GetRuntimeError(string name)
    {
        return _runtimeErrors.TryGetValue(ResolveLoadedPluginName(name), out string? error) ? error : null;
    }

    internal string? GetRuntimeErrorCode(string name)
    {
        string lookupName = ResolveLoadedPluginName(name);
        return _runtimeErrors.ContainsKey(lookupName)
            ? _runtimeErrorCodes.GetValueOrDefault(lookupName, "plugin_runtime_error")
            : null;
    }

    public bool IsKnownPlugin(string name)
    {
        string lookupName = ResolveLoadedPluginName(name);
        return HasActualPluginName(lookupName);
    }

    public bool IsDataSpecializedPlugin(string name)
    {
        string lookupName = ResolveLoadedPluginName(name);
        return _dataPlugins.Any(plugin => string.Equals(plugin.Name, lookupName, StringComparison.OrdinalIgnoreCase));
    }

    public bool SetEnabled(string name, bool enabled, string source = Audit.System)
    {
        return SetEnabled(name, enabled, source, out _);
    }

    public bool SetEnabled(string name, bool enabled, string source, out string? failureCode)
    {
        string lookupName = ResolveLoadedPluginName(name);
        if (!IsKnownPlugin(lookupName))
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
                string key = settings.PluginPreferences.Keys.FirstOrDefault(item => string.Equals(item, lookupName, StringComparison.OrdinalIgnoreCase)) ?? lookupName;
                settings.PluginPreferences[key] = new PluginPreference
                {
                    Enabled = enabled,
                };
                ConfigStore.Save(settings);
            }
        });
        if (!changed)
        {
            failureCode = "host_maintenance";
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
        name = ResolveLoadedPluginName(name);
        return _dataPlugins.Any(plugin => string.Equals(plugin.Name, name, StringComparison.OrdinalIgnoreCase) && plugin.Frontend is not null)
            || _managedPlugins.Any(plugin => string.Equals(plugin.Manifest.Name, name, StringComparison.OrdinalIgnoreCase) && plugin.Manifest.Frontend is not null);
    }

    internal bool TryGetPluginDirectory(string name, out string? directory)
    {
        name = ResolveLoadedPluginName(name);
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

    private bool ResolveLoadedPluginNameHasActual(string name)
    {
        return _dataPlugins.Any(plugin => string.Equals(plugin.Name, name, StringComparison.OrdinalIgnoreCase))
            || _managedPlugins.Any(plugin => string.Equals(plugin.Manifest.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private string ResolveLoadedPluginName(string name)
    {
        string canonical = PluginNameMigration.Canonicalize(name);
        if (ResolveLoadedPluginNameHasActual(canonical))
        {
            return canonical;
        }
        string? legacy = PluginNameMigration.LegacyNameFor(canonical);
        return legacy is not null && ResolveLoadedPluginNameHasActual(legacy)
            ? legacy
            : canonical;
    }

    private bool HasActualPluginName(string name)
    {
        return ResolveLoadedPluginNameHasActual(name);
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

}

internal enum PluginLifecyclePhase
{
    Initialize,
    Start,
    Stop,
}

internal sealed class PluginLifecycleTimeoutException : TimeoutException
{
    public PluginLifecycleTimeoutException(PluginLifecyclePhase phase)
        : base($"插件生命周期阶段 {phase} 超过 20 秒截止时间")
    {
        Phase = phase;
    }

    public PluginLifecyclePhase Phase { get; }
}
