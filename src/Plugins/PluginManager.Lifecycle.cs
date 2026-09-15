using System.Reflection;
using NexusPipeline.Extensibility;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Plugins.Managed;
using NexusPipeline.Services;
using NexusPipeline.Services.Networking;
using NexusPipeline.Services.Notification;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

internal sealed partial class PluginManager
{
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

}
