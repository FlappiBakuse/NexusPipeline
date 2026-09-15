using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Plugins.Managed;
using NexusPipeline.Services.Update;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

/// <summary>PluginManager 的发现/扫描与加载编排：只负责把插件描述交给生命周期和运行态层。</summary>
internal sealed partial class PluginManager
{
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
                    UpdateService.CurrentVersion,
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
                    UpdateService.CurrentVersion,
                    out string hostCompatibilityReason))
            {
                _runtimeStates[name] = PluginRuntimeState.Incompatible;
                _runtimeErrors[name] = hostCompatibilityReason;
                _runtimeErrorCodes[name] = "plugin_incompatible_host";
                Logger.Warn($"[插件] 插件「{descriptor.Manifest.DisplayName}」需要更高宿主版本，程序集未加载：{hostCompatibilityReason}");
                continue;
            }
            _capabilities.RegisterKeys(name, descriptor.Manifest.Capabilities);
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
                _runtimeErrorCodes[name] = "plugin_incompatible_api";
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
            _runtimeStates[manifest.Name] = PluginRuntimeState.Discovered;
        }
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
}
