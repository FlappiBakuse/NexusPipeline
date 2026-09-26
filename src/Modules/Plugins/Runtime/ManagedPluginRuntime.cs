using System.Reflection;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Modules.Notifications;
using NexusPipeline.Modules.Plugins.Managed;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Testing;

namespace NexusPipeline.Modules.Plugins.Runtime;

internal sealed record ManagedPluginDescriptor(PluginManifest Manifest, string Directory);

/// <summary>负责 managed-code 插件的加载、生命周期调用和卸载。</summary>
internal sealed class ManagedPluginRuntime
{
    private readonly ManagedPluginDescriptor _descriptor;
    private readonly IPluginNotificationSink _notifications;
    private readonly PluginUserGlobalManagementRegistry _userGlobalManagement;
    private readonly PluginUserListBadgeRegistry _userListBadges;
    private readonly PluginExecutionEventRegistry _executionEvents;
    private readonly OutboundHttpClientProvider _http;
    private readonly PluginUiContributionRegistry _ui;
    private readonly PluginWebApiRegistry _webApi;
    private readonly PluginHistoryContributionRegistry _history;
    private readonly PluginEmulatorSupportRegistry _emulatorSupport;
    private readonly PluginExecutionProviderRegistry _executionProviders;
    private readonly Action<Exception> _reportJobError;
    private PluginLoadContext? _loadContext;
    private INexusPlugin? _plugin;
    private PluginHostContext? _hostContext;

    public bool StopTimedOut { get; private set; }

    public ManagedPluginRuntime(
        ManagedPluginDescriptor descriptor,
        IPluginNotificationSink notifications,
        PluginUserGlobalManagementRegistry userGlobalManagement,
        PluginUserListBadgeRegistry userListBadges,
        PluginExecutionEventRegistry executionEvents,
        OutboundHttpClientProvider http,
        PluginUiContributionRegistry ui,
        PluginWebApiRegistry webApi,
        PluginHistoryContributionRegistry history,
        PluginEmulatorSupportRegistry emulatorSupport,
        PluginExecutionProviderRegistry executionProviders,
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
        _emulatorSupport = emulatorSupport;
        _executionProviders = executionProviders;
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
                _history,
                _emulatorSupport,
                _executionProviders,
                _descriptor.Manifest.Localization);
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
