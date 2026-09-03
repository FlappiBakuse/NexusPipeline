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

    public string UpdatedAt { get; init; } = "";

    public IReadOnlyList<PluginChangelogEntry> Changelog { get; init; } = Array.Empty<PluginChangelogEntry>();

    public bool HasReadme { get; init; }
}

internal sealed record PluginFrontendRuntimeDescriptor(
    string Name,
    string DisplayName,
    string Version,
    string FrontendApiVersion,
    string EntryUrl,
    IReadOnlyList<string> StyleUrls);

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
    private readonly PluginUiContributionRegistry _uiContributions = new();
    private readonly PluginWebApiRegistry _webApi = new();
    private readonly PluginHistoryContributionRegistry _historyContributions = new();
    private readonly object _managementSnapshotSync = new();
    private long _managementRevision;
    private IReadOnlyList<PluginSummary>? _pluginSummariesCache;
    private IReadOnlyList<PluginManagementView>? _pluginManagementViewsCache;
    private string? _managementStateFingerprint;

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
            lock (_managementSnapshotSync)
            {
                return _pluginSummariesCache ??= BuildPluginSummaries();
            }
        }
    }

    /// <summary>插件管理投影的当前内存修订号，供宿主缓存和调试观测使用。</summary>
    internal long PluginManagementRevision
    {
        get
        {
            lock (_managementSnapshotSync)
            {
                return _managementRevision;
            }
        }
    }

    /// <summary>插件文件状态或运行时配置发生变化时清除本地投影缓存。</summary>
    internal void InvalidateManagementSnapshot()
    {
        lock (_managementSnapshotSync)
        {
            _managementRevision++;
            _pluginSummariesCache = null;
            _pluginManagementViewsCache = null;
            _managementStateFingerprint = null;
        }
    }

    private IReadOnlyList<PluginSummary> BuildPluginSummaries()
    {
        var list = new List<PluginSummary>();
        foreach (DataSpecializedPlugin plugin in _dataPlugins)
        {
            PluginPresentationMetadata metadata = PluginPresentationMetadataParser.LoadLocal(
                plugin.PluginDirectory,
                plugin.GameName,
                plugin.Version);
            list.Add(new PluginSummary(
                plugin.Name,
                plugin.ArtifactName,
                plugin.DisplayName,
                string.IsNullOrWhiteSpace(metadata.GameName) ? plugin.GameName : metadata.GameName,
                plugin.Description,
                plugin.Version,
                "data-specialized",
                "",
                plugin.CapabilityKeys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray(),
                plugin.Frontend is not null,
                plugin.Frontend?.ApiVersion ?? "")
            {
                Authors = metadata.Authors,
                Tags = metadata.Tags,
                Homepage = metadata.Homepage,
                UpdatedAt = metadata.UpdatedAt,
                Changelog = metadata.Changelog,
                HasReadme = metadata.HasReadme,
            });
        }
        foreach (ManagedPluginDescriptor plugin in _managedPlugins)
        {
            string artifactName = plugin.Manifest.ArtifactName;
            PluginPresentationMetadata metadata = PluginPresentationMetadataParser.LoadLocal(
                plugin.Directory,
                plugin.Manifest.GameName,
                plugin.Manifest.Version);
            list.Add(new PluginSummary(
                plugin.Manifest.Name,
                artifactName,
                plugin.Manifest.DisplayName,
                string.IsNullOrWhiteSpace(metadata.GameName) ? plugin.Manifest.GameName : metadata.GameName,
                plugin.Manifest.Description,
                plugin.Manifest.Version,
                "managed-code",
                plugin.Manifest.ApiVersion,
                plugin.Manifest.Capabilities.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray(),
                plugin.Manifest.Frontend is not null,
                plugin.Manifest.Frontend?.ApiVersion ?? "")
            {
                Authors = metadata.Authors,
                Tags = metadata.Tags,
                Homepage = metadata.Homepage,
                UpdatedAt = metadata.UpdatedAt,
                Changelog = metadata.Changelog,
                HasReadme = metadata.HasReadme,
            });
        }
        return list;
    }

    /// <summary>插件管理控制面共享投影；ownership/pending 由同一份快照合并，避免各适配器自行拼装。</summary>
    internal IReadOnlyList<PluginManagementView> PluginManagementViews
    {
        get
        {
            string fingerprint = ReadManagementStateFingerprint();
            lock (_managementSnapshotSync)
            {
                if (!string.Equals(_managementStateFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    _managementRevision++;
                    _pluginSummariesCache = null;
                    _pluginManagementViewsCache = null;
                    _managementStateFingerprint = fingerprint;
                }
                if (_pluginManagementViewsCache is not null)
                {
                    return _pluginManagementViewsCache;
                }

                IReadOnlyDictionary<string, PluginOwnership> ownership = PluginInstallRecovery.ReadOwnership();
                IReadOnlyList<PluginPendingOperation> pending = PluginInstallRecovery.ReadPending();
                _pluginManagementViewsCache = PluginSummaries
                    .Select(summary => PluginManagementView.Create(summary, this, ownership, pending))
                    .ToArray();
                return _pluginManagementViewsCache;
            }
        }
    }

    private static string ReadManagementStateFingerprint()
    {
        return string.Join(
            "|",
            DescribeStateFile(AppPaths.PluginOwnershipPath),
            DescribeStateFile(AppPaths.PluginPendingPath));
    }

    private static string DescribeStateFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return path + ":missing";
            }
            FileInfo file = new(path);
            return $"{path}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return path + ":unavailable";
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

    internal IReadOnlyList<PluginUiContributionRegistration> UiContributions =>
        _uiContributions.Snapshot();

    internal bool TryGetUiContribution(
        string pluginName,
        string contributionId,
        out PluginUiContributionRegistration? registration) =>
        _uiContributions.TryGet(pluginName, contributionId, out registration);

    internal IReadOnlyList<PluginWebApiRegistration> WebApiContributions =>
        _webApi.Snapshot();

    internal bool TryGetWebApi(
        string pluginName,
        string method,
        string route,
        out PluginWebApiRegistration? registration) =>
        _webApi.TryGet(pluginName, method, route, out registration);

    internal IReadOnlyList<PluginHistoryContributionRegistration> HistoryContributions =>
        _historyContributions.Snapshot();

    /// <summary>在历史提交前收集已注册插件的展示快照；插件异常或超限只会丢弃该插件的展示内容。</summary>
    internal void EnrichHistory(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.StartTime == DateTime.MinValue)
        {
            return;
        }

        var context = new PluginHistoryContext(
            record.Id,
            record.UserId,
            record.UserName,
            record.ScriptInstanceId,
            record.ScriptName,
            record.QueueId,
            record.QueueName,
            record.Mode,
            new DateTimeOffset(record.StartTime),
            record.EndTime.HasValue ? new DateTimeOffset(record.EndTime.Value) : null,
            string.IsNullOrWhiteSpace(record.FinalStatus) ? record.Status : record.FinalStatus);
        var snapshots = new List<PluginHistoryRecord>();
        int totalBytes = 0;
        foreach (PluginHistoryContributionRegistration registration in HistoryContributions)
        {
            PluginHistoryDisplay? display;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                display = registration.Contribution.Handler(context, timeout.Token)
                    .AsTask()
                    .WaitAsync(timeout.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[插件:{registration.PluginName}] 历史展示贡献执行失败（{registration.Contribution.Id}）：{ex.Message}");
                continue;
            }
            if (!PluginUiValidation.TrySanitizeHistoryDisplay(display, out PluginHistoryDisplay? sanitized, out string error)
                || sanitized is null)
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    Logger.Warn($"[插件:{registration.PluginName}] 历史展示贡献无效（{registration.Contribution.Id}）：{error}");
                }
                continue;
            }

            var snapshot = new PluginHistoryRecord
            {
                PluginName = registration.PluginName,
                PluginDisplayName = registration.PluginDisplayName,
                Id = sanitized.Id,
                Title = sanitized.Title,
                Order = registration.Contribution.Order,
                Badges = sanitized.Badges?.Select(badge => new PluginHistoryBadgeRecord
                {
                    Label = badge.Label,
                    Tone = badge.Tone,
                    Title = badge.Title,
                }).ToList() ?? new List<PluginHistoryBadgeRecord>(),
                Fields = sanitized.Fields?.Select(field => new PluginHistoryFieldRecord
                {
                    Label = field.Label,
                    Value = field.Value,
                    Tone = field.Tone,
                }).ToList() ?? new List<PluginHistoryFieldRecord>(),
            };
            int bytes;
            try
            {
                bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOpts.Default).Length;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[插件:{registration.PluginName}] 历史展示贡献序列化失败（{registration.Contribution.Id}）：{ex.Message}");
                continue;
            }
            if (bytes > 16 * 1024 || totalBytes + bytes > 64 * 1024)
            {
                Logger.Warn($"[插件:{registration.PluginName}] 历史展示贡献超出大小上限（{registration.Contribution.Id}）");
                continue;
            }
            snapshots.Add(snapshot);
            totalBytes += bytes;
        }
        record.PluginHistory = snapshots;
    }

    internal IReadOnlyList<PluginFrontendRuntimeDescriptor> FrontendDescriptors
    {
        get
        {
            var result = new List<PluginFrontendRuntimeDescriptor>();
            foreach (DataSpecializedPlugin plugin in _dataPlugins)
            {
                TryAddFrontendDescriptor(
                    result,
                    plugin.Name,
                    plugin.DisplayName,
                    plugin.Version,
                    plugin.Frontend);
            }
            foreach (ManagedPluginDescriptor plugin in _managedPlugins)
            {
                TryAddFrontendDescriptor(
                    result,
                    plugin.Manifest.Name,
                    plugin.Manifest.DisplayName,
                    plugin.Manifest.Version,
                    plugin.Manifest.Frontend);
            }
            return result.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    internal bool TryResolveFrontendAsset(
        string pluginName,
        string relativePath,
        out string? filePath)
    {
        filePath = null;
        if (!IsRuntimeEnabled(pluginName)
            || !PluginFrontendManifest.IsPublicFrontendPath(relativePath))
        {
            return false;
        }
        string? pluginDirectory = _dataPlugins
            .FirstOrDefault(plugin => string.Equals(plugin.Name, pluginName, StringComparison.OrdinalIgnoreCase))?.PluginDirectory;
        DataSpecializedPlugin? data = _dataPlugins.FirstOrDefault(plugin =>
            string.Equals(plugin.Name, pluginName, StringComparison.OrdinalIgnoreCase));
        if (data is null)
        {
            pluginDirectory = _managedPlugins.FirstOrDefault(plugin =>
                string.Equals(plugin.Manifest.Name, pluginName, StringComparison.OrdinalIgnoreCase))?.Directory;
        }
        if (string.IsNullOrWhiteSpace(pluginDirectory))
        {
            return false;
        }
        string root = Path.GetFullPath(pluginDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(pluginDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(candidate)
            || !IsPublicFrontendExtension(Path.GetExtension(candidate)))
        {
            return false;
        }
        filePath = candidate;
        return true;
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

    /// <summary>返回已发现、已启用且有效的数据化插件配置校验脚本；普通脚本和 managed-code 插件不参与。</summary>
    internal bool TryGetConfigValidator(string pluginName, out ConfigValidatorDescriptor? descriptor)
    {
        descriptor = null;
        DataSpecializedPlugin? plugin = _dataPlugins.FirstOrDefault(item =>
            string.Equals(item.Name, pluginName, StringComparison.OrdinalIgnoreCase));
        if (plugin is null || !IsRuntimeEnabled(plugin.Name) || !plugin.HasConfigValidator)
        {
            return false;
        }
        descriptor = plugin.ReadConfigValidator();
        return descriptor is not null;
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
        foreach (DataSpecializedPlugin plugin in _dataPlugins)
        {
            _runtimeStates[plugin.Name] = PluginRuntimeState.Shutdown;
        }
        InvalidateManagementSnapshot();
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
        _configuredEnabled[name] = enabled;
        InvalidateManagementSnapshot();
        Audit.Log(source, $"{(enabled ? "启用" : "禁用")}插件", name);
        Logger.Info($"[插件] 已{(enabled ? "启用" : "禁用")}：{name}（重启后生效）。");
        failureCode = null;
        return true;
    }

    internal bool HasFrontend(string name)
    {
        return _dataPlugins.Any(plugin => string.Equals(plugin.Name, name, StringComparison.OrdinalIgnoreCase) && plugin.Frontend is not null)
            || _managedPlugins.Any(plugin => string.Equals(plugin.Manifest.Name, name, StringComparison.OrdinalIgnoreCase) && plugin.Manifest.Frontend is not null);
    }

    internal bool TryGetPluginDirectory(string name, out string? directory)
    {
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
            if (!string.Equals(Path.GetFileName(directory), manifest.ArtifactName, StringComparison.Ordinal))
            {
                Logger.Error($"[插件] 跳过物理目录名不匹配的插件：{Path.GetFileName(directory)}（期望 {manifest.ArtifactName}）");
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
                _uiContributions,
                _webApi,
                _historyContributions,
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

    private bool IsRuntimeEnabled(string name)
    {
        return _runtimeStates.TryGetValue(name, out PluginRuntimeState state)
            && state == PluginRuntimeState.Active;
    }

    private bool IsManagedCode(string name)
    {
        return _managedPlugins.Any(plugin => string.Equals(plugin.Manifest.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static PluginFrontendRuntimeDescriptor ToFrontendDescriptor(
        string name,
        string displayName,
        string version,
        PluginFrontendManifest frontend)
    {
        string prefix = "/plugin-assets/" + Uri.EscapeDataString(name) + "/";
        return new PluginFrontendRuntimeDescriptor(
            name,
            displayName,
            version,
            frontend.ApiVersion,
            prefix + frontend.Entry,
            frontend.Styles.Select(style => prefix + style).ToArray());
    }

    private void TryAddFrontendDescriptor(
        List<PluginFrontendRuntimeDescriptor> result,
        string name,
        string displayName,
        string version,
        PluginFrontendManifest? frontend)
    {
        try
        {
            if (!IsRuntimeEnabled(name) || frontend is null)
            {
                return;
            }
            result.Add(ToFrontendDescriptor(name, displayName, version, frontend));
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件:{name}] 前端运行时清单生成失败：{ex.Message}");
        }
    }

    private static bool IsPublicFrontendExtension(string extension)
    {
        return extension.ToLowerInvariant() is ".js" or ".mjs" or ".css" or ".json"
            or ".svg" or ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".ico"
            or ".woff" or ".woff2";
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
            if (!string.Equals(Path.GetFileName(directory), manifest.ArtifactName, StringComparison.Ordinal))
            {
                Logger.Error($"[插件] 跳过物理目录名不匹配的数据插件：{Path.GetFileName(directory)}（期望 {manifest.ArtifactName}）");
                continue;
            }
            DataSpecializedPlugin? plugin = DataSpecializedPlugin.Load(directory, manifest);
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
        private readonly PluginUiContributionRegistry _ui;
        private readonly PluginWebApiRegistry _webApi;
        private readonly PluginHistoryContributionRegistry _history;
        private readonly Action<Exception> _reportJobError;
        private PluginLoadContext? _loadContext;
        private INexusPlugin? _plugin;
        private PluginHostContext? _hostContext;

        public bool StopTimedOut { get; private set; }

        public ManagedPluginRuntime(
            ManagedPluginDescriptor descriptor,
            NotificationDispatcher notifications,
            PluginUserGlobalManagementRegistry userGlobalManagement,
            PluginUserListBadgeRegistry userListBadges,
            PluginExecutionEventRegistry executionEvents,
            OutboundHttpClientProvider http,
            PluginUiContributionRegistry ui,
            PluginWebApiRegistry webApi,
            PluginHistoryContributionRegistry history,
            Action<Exception> reportJobError)
        {
            _descriptor = descriptor;
            _notifications = notifications;
            _userGlobalManagement = userGlobalManagement;
            _userListBadges = userListBadges;
            _executionEvents = executionEvents;
            _http = http;
            _ui = ui;
            _webApi = webApi;
            _history = history;
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
                    _http,
                    _ui,
                    _webApi,
                    _history);
                AwaitLifecycle(
                    token => plugin.InitializeAsync(_hostContext, token),
                    PluginLifecyclePhase.Initialize);
                AwaitLifecycle(
                    plugin.StartAsync,
                    PluginLifecyclePhase.Start);
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
                if (_plugin is not null)
                {
                    AwaitLifecycle(_plugin.StopAsync, PluginLifecyclePhase.Stop);
                }
            }
            catch (PluginLifecycleTimeoutException)
            {
                StopTimedOut = true;
                throw;
            }
            finally
            {
                Cleanup();
            }
        }

        private static void AwaitLifecycle(
            Func<CancellationToken, ValueTask> lifecycleFactory,
            PluginLifecyclePhase phase)
        {
            using var timeout = new CancellationTokenSource(TestHooks.ScaledMs(20_000));
            Task task = lifecycleFactory(timeout.Token).AsTask();
            try
            {
                task.WaitAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                ObserveFault(task);
                throw new PluginLifecycleTimeoutException(phase);
            }
        }

        private static void ObserveFault(Task task)
        {
            _ = task.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
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
