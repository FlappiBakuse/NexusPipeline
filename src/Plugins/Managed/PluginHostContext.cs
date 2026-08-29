using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Persistence;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Plugins;
using NexusPipeline.Services.Networking;
using NexusPipeline.Services.Notification;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins.Managed;

internal sealed class PluginHostContext : IPluginHostContextV1_3
{
    public PluginHostContext(
        string pluginName,
        string pluginDisplayName,
        NotificationDispatcher notifications,
        Action<Exception> reportJobError,
        PluginUserGlobalManagementRegistry globalManagement,
        PluginUserListBadgeRegistry userListBadges,
        PluginExecutionEventRegistry executionEvents,
        OutboundHttpClientProvider http,
        PluginUiContributionRegistry ui,
        PluginWebApiRegistry webApi,
        PluginHistoryContributionRegistry history)
    {
        PluginName = pluginName;
        Logger = new PluginLogger(pluginName);
        Config = new PluginConfigStore(pluginName);
        Secrets = new PluginSecretStore(pluginName);
        Notifications = new PluginNotificationAdapter(notifications);
        Scheduler = new PluginJobScheduler(pluginName, reportJobError);
        UserData = new PluginUserDataStore(pluginName);
        _globalManagement = new PluginUserGlobalManagementAdapter(globalManagement, pluginName, pluginDisplayName);
        _userListBadges = new PluginUserListBadgeAdapter(userListBadges, pluginName, pluginDisplayName);
        _executionEvents = new PluginExecutionEventAdapter(executionEvents, pluginName);
        Http = new PluginHttpClientFactory(http);
        _ui = new PluginUiContributionAdapter(ui, pluginName, pluginDisplayName);
        ScopedData = new PluginScopedDataStore(pluginName);
        _webApi = new PluginWebApiAdapter(webApi, pluginName);
        _history = new PluginHistoryContributionAdapter(history, pluginName, pluginDisplayName);
    }

    public string PluginName { get; }

    public IPluginLogger Logger { get; }

    public IPluginConfigStore Config { get; }

    public IPluginSecretStore Secrets { get; }

    public IPluginNotificationService Notifications { get; }

    public IPluginJobScheduler Scheduler { get; }

    public IPluginUserDataStore UserData { get; }

    public IPluginUserGlobalManagementRegistry UserGlobalManagement => _globalManagement;

    public IPluginUserListBadgeRegistry UserListBadges => _userListBadges;

    public IPluginExecutionEventService ExecutionEvents => _executionEvents;

    public IPluginHttpClientFactory Http { get; }

    public IPluginUiContributionRegistry Ui => _ui;

    public IPluginScopedDataStore ScopedData { get; }

    public IPluginWebApiRegistry WebApi => _webApi;

    public IPluginHistoryContributionRegistry History => _history;

    private readonly PluginUserGlobalManagementAdapter _globalManagement;

    private readonly PluginUserListBadgeAdapter _userListBadges;
    private readonly PluginExecutionEventAdapter _executionEvents;

    private readonly PluginUiContributionAdapter _ui;

    private readonly PluginWebApiAdapter _webApi;

    private readonly PluginHistoryContributionAdapter _history;

    public void Dispose()
    {
        _executionEvents.Dispose();
        _userListBadges.Dispose();
        _globalManagement.Dispose();
        _ui.Dispose();
        _webApi.Dispose();
        _history.Dispose();
        ((PluginJobScheduler)Scheduler).Dispose();
    }

}

internal sealed class PluginUserListBadgeAdapter : IPluginUserListBadgeRegistry, IDisposable
{
    private readonly PluginUserListBadgeRegistry _registry;
    private readonly string _pluginName;
    private readonly string _pluginDisplayName;
    private readonly List<IDisposable> _registrations = new();
    private readonly object _sync = new();

    public PluginUserListBadgeAdapter(
        PluginUserListBadgeRegistry registry,
        string pluginName,
        string pluginDisplayName)
    {
        _registry = registry;
        _pluginName = pluginName;
        _pluginDisplayName = pluginDisplayName;
    }

    public IDisposable Register(PluginUserListBadgeContribution contribution)
    {
        IDisposable registration = _registry.Register(_pluginName, _pluginDisplayName, contribution);
        lock (_sync)
        {
            _registrations.Add(registration);
        }
        return new CallbackDisposable(() =>
        {
            registration.Dispose();
            lock (_sync)
            {
                _registrations.Remove(registration);
            }
        });
    }

    public void Dispose()
    {
        IDisposable[] registrations;
        lock (_sync)
        {
            registrations = _registrations.ToArray();
            _registrations.Clear();
        }
        foreach (IDisposable registration in registrations)
        {
            registration.Dispose();
        }
    }
}

internal sealed class PluginUserGlobalManagementAdapter : IPluginUserGlobalManagementRegistry, IDisposable
{
    private readonly PluginUserGlobalManagementRegistry _registry;
    private readonly string _pluginName;
    private readonly string _pluginDisplayName;
    private readonly List<IDisposable> _registrations = new();
    private readonly object _sync = new();

    public PluginUserGlobalManagementAdapter(
        PluginUserGlobalManagementRegistry registry,
        string pluginName,
        string pluginDisplayName)
    {
        _registry = registry;
        _pluginName = pluginName;
        _pluginDisplayName = pluginDisplayName;
    }

    public IDisposable Register(PluginUserGlobalManagementContribution contribution)
    {
        IDisposable registration = _registry.Register(_pluginName, _pluginDisplayName, contribution);
        lock (_sync)
        {
            _registrations.Add(registration);
        }
        return new CallbackDisposable(() =>
        {
            registration.Dispose();
            lock (_sync)
            {
                _registrations.Remove(registration);
            }
        });
    }

    public void Dispose()
    {
        IDisposable[] registrations;
        lock (_sync)
        {
            registrations = _registrations.ToArray();
            _registrations.Clear();
        }
        foreach (IDisposable registration in registrations)
        {
            registration.Dispose();
        }
    }
}

internal sealed class PluginExecutionEventAdapter : IPluginExecutionEventService, IDisposable
{
    private readonly PluginExecutionEventRegistry _registry;
    private readonly string _pluginName;
    private readonly List<IDisposable> _subscriptions = new();
    private readonly object _sync = new();

    public PluginExecutionEventAdapter(PluginExecutionEventRegistry registry, string pluginName)
    {
        _registry = registry;
        _pluginName = pluginName;
    }

    public IDisposable SubscribeUserRunStarting(Func<PluginUserRunStartingEvent, ValueTask> handler)
    {
        IDisposable subscription = _registry.Subscribe(_pluginName, handler);
        lock (_sync)
        {
            _subscriptions.Add(subscription);
        }
        return new CallbackDisposable(() =>
        {
            subscription.Dispose();
            lock (_sync)
            {
                _subscriptions.Remove(subscription);
            }
        });
    }

    public void Dispose()
    {
        IDisposable[] subscriptions;
        lock (_sync)
        {
            subscriptions = _subscriptions.ToArray();
            _subscriptions.Clear();
        }
        foreach (IDisposable subscription in subscriptions)
        {
            subscription.Dispose();
        }
    }
}

internal sealed class PluginLogger : IPluginLogger
{
    private readonly string _prefix;

    public PluginLogger(string pluginName)
    {
        _prefix = $"[插件:{pluginName}] ";
    }

    public void Debug(string message) => Logger.Debug(_prefix + message);

    public void Info(string message) => Logger.Info(_prefix + message);

    public void Warn(string message) => Logger.Warn(_prefix + message);

    public void Error(string message) => Logger.Error(_prefix + message);
}

internal static class PluginConfigPath
{
    public static string For(string pluginName)
    {
        string safe = pluginName;
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            safe = safe.Replace(c, '_');
        }
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "plugin";
        }
        return Path.Combine(AppPaths.ConfigDir, "plugins", safe + ".json");
    }

    public static string ForSecrets(string pluginName)
    {
        string path = For(pluginName);
        return Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".secrets.json");
    }
}

internal sealed class PluginConfigStore : IPluginConfigStore
{
    private readonly string _path;

    public PluginConfigStore(string pluginName)
    {
        _path = PluginConfigPath.For(pluginName);
    }

    public ValueTask<T?> ReadAsync<T>(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_path))
        {
            return ValueTask.FromResult<T?>(default);
        }
        JsonStore.TryRead(_path, out T? value, "插件配置");
        return ValueTask.FromResult(value);
    }

    public ValueTask WriteAsync<T>(T value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        JsonUtil.WriteAtomic(_path, JsonSerializer.Serialize(value, JsonOpts.Indented));
        return ValueTask.CompletedTask;
    }
}

internal sealed class PluginSecretStore : IPluginSecretStore
{
    private readonly string _path;

    public PluginSecretStore(string pluginName)
    {
        _path = PluginConfigPath.ForSecrets(pluginName);
    }

    public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        JsonObject root = ReadRoot();
        string stored = root[key]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(stored))
        {
            return ValueTask.FromResult<string?>(null);
        }
        return ValueTask.FromResult(SecretStore.TryDecrypt(stored, out string? plain) ? plain : null);
    }

    public ValueTask SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        JsonObject root = ReadRoot();
        if (string.IsNullOrWhiteSpace(value))
        {
            root.Remove(key);
        }
        else
        {
            root[key] = SecretStore.Encrypt(value);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        JsonUtil.WriteAtomic(_path, root.ToJsonString(JsonOpts.Indented));
        return ValueTask.CompletedTask;
    }

    private JsonObject ReadRoot()
    {
        return JsonStore.ReadObjectOrEmpty(_path, "插件密钥文件");
    }
}

internal sealed class PluginNotificationAdapter : IPluginNotificationService
{
    private readonly NotificationDispatcher _dispatcher;

    public PluginNotificationAdapter(NotificationDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public ValueTask SendAsync(PluginNotification notification, CancellationToken cancellationToken = default)
    {
        return _dispatcher.SendPluginAsync(notification, cancellationToken);
    }
}

internal sealed class PluginUiContributionAdapter : IPluginUiContributionRegistry, IDisposable
{
    private readonly PluginUiContributionRegistry _registry;
    private readonly string _pluginName;
    private readonly string _pluginDisplayName;
    private readonly List<IDisposable> _registrations = new();
    private readonly object _sync = new();

    public PluginUiContributionAdapter(
        PluginUiContributionRegistry registry,
        string pluginName,
        string pluginDisplayName)
    {
        _registry = registry;
        _pluginName = pluginName;
        _pluginDisplayName = pluginDisplayName;
    }

    public IDisposable Register(PluginUiContribution contribution)
    {
        IDisposable registration = _registry.Register(_pluginName, _pluginDisplayName, contribution);
        lock (_sync)
        {
            _registrations.Add(registration);
        }
        return new CallbackDisposable(() =>
        {
            registration.Dispose();
            lock (_sync)
            {
                _registrations.Remove(registration);
            }
        });
    }

    public void Dispose()
    {
        IDisposable[] registrations;
        lock (_sync)
        {
            registrations = _registrations.ToArray();
            _registrations.Clear();
        }
        foreach (IDisposable registration in registrations)
        {
            registration.Dispose();
        }
    }
}

internal sealed class PluginWebApiAdapter : IPluginWebApiRegistry, IDisposable
{
    private readonly PluginWebApiRegistry _registry;
    private readonly string _pluginName;
    private readonly List<IDisposable> _registrations = new();
    private readonly object _sync = new();

    public PluginWebApiAdapter(PluginWebApiRegistry registry, string pluginName)
    {
        _registry = registry;
        _pluginName = pluginName;
    }

    public IDisposable Register(PluginWebApiRoute route)
    {
        IDisposable registration = _registry.Register(_pluginName, route);
        lock (_sync)
        {
            _registrations.Add(registration);
        }
        return new CallbackDisposable(() =>
        {
            registration.Dispose();
            lock (_sync)
            {
                _registrations.Remove(registration);
            }
        });
    }

    public void Dispose()
    {
        IDisposable[] registrations;
        lock (_sync)
        {
            registrations = _registrations.ToArray();
            _registrations.Clear();
        }
        foreach (IDisposable registration in registrations)
        {
            registration.Dispose();
        }
    }
}

internal sealed class PluginHistoryContributionAdapter : IPluginHistoryContributionRegistry, IDisposable
{
    private readonly PluginHistoryContributionRegistry _registry;
    private readonly string _pluginName;
    private readonly string _pluginDisplayName;
    private readonly List<IDisposable> _registrations = new();
    private readonly object _sync = new();

    public PluginHistoryContributionAdapter(
        PluginHistoryContributionRegistry registry,
        string pluginName,
        string pluginDisplayName)
    {
        _registry = registry;
        _pluginName = pluginName;
        _pluginDisplayName = pluginDisplayName;
    }

    public IDisposable Register(PluginHistoryContribution contribution)
    {
        IDisposable registration = _registry.Register(_pluginName, _pluginDisplayName, contribution);
        lock (_sync)
        {
            _registrations.Add(registration);
        }
        return new CallbackDisposable(() =>
        {
            registration.Dispose();
            lock (_sync)
            {
                _registrations.Remove(registration);
            }
        });
    }

    public void Dispose()
    {
        IDisposable[] registrations;
        lock (_sync)
        {
            registrations = _registrations.ToArray();
            _registrations.Clear();
        }
        foreach (IDisposable registration in registrations)
        {
            registration.Dispose();
        }
    }
}
