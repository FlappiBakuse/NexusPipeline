using NexusPipeline.ControlPlane.Http;
using NexusPipeline.ControlPlane.Mcp;
using NexusPipeline.Host.Composition;
using NexusPipeline.Host.State;
using NexusPipeline.Host.Lifecycle;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Notifications;
using NexusPipeline.Modules.Plugins.Repository;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Modules.Scheduling;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Settings.Persistence;
using NexusPipeline.Modules.Settings.UseCases;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Modules.Users.UseCases;

namespace NexusPipeline.Host;

/// <summary>
/// A single process-owned Host object graph. The process entry point creates exactly one
/// instance and owns its asynchronous disposal; business modules receive typed collaborators
/// and never resolve services from this object.
/// </summary>
internal sealed class HostRuntime : IAsyncDisposable
{
    private readonly HostCompositionRoot _composition;
    private Bootstrap? _bootstrap;
    private int _started;
    private int _disposed;

    internal HostRuntime(HostCompositionRoot composition)
    {
        _composition = composition;
    }

    internal HostCompositionRoot Composition => _composition;

    public AppSettings Settings => _composition.Settings;
    public ExecutionDispatcher Center => _composition.Center;
    public ExecutionValidator Validator => _composition.Validator;
    public RunHistoryService History => _composition.History;
    public PluginManager Plugins => _composition.Plugins;
    public NotificationDispatcher Notifications => _composition.Notifications;
    public Scheduler Scheduler => _composition.Scheduler;
    public UpdateService UpdateService => _composition.UpdateService;
    public UpdateAutomationService UpdateAutomation => _composition.UpdateAutomation;
    public UserCommands UserCommands => _composition.UserCommands;
    internal HttpRouteBindings HttpRoutes => _composition.HttpRoutes;
    internal AutomationDefinitionState EntityState => _composition.EntityState;
    internal SettingsState SettingsState => _composition.SettingsState;

    internal Bootstrap Bootstrap
        => Volatile.Read(ref _bootstrap) ?? throw new InvalidOperationException("Host runtime has not been bound to Bootstrap.");

    internal void BindBootstrap(Bootstrap bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        if (Interlocked.CompareExchange(ref _bootstrap, bootstrap, null) is not null)
        {
            throw new InvalidOperationException("Host runtime is already bound to Bootstrap.");
        }
    }

    internal McpToolContext CreateMcpToolContext(Func<bool>? requestRestart)
        => _composition.CreateMcpToolContext(requestRestart);

    internal void ReloadSettings(ConfigLoadMode mode = ConfigLoadMode.Repair)
        => _composition.ReloadSettings(mode);

    internal void ReplaceSettings(AppSettings settings)
        => _composition.ReplaceSettings(settings);

    internal void ReloadData()
        => _composition.ReloadData();

    internal T Get<T>() where T : notnull
        => _composition.Get<T>();

    public void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException("Host runtime has already started or is stopping.");
        }
        try
        {
            Bootstrap.StartServices();
        }
        catch
        {
            try
            {
                Bootstrap.Shutdown(null, null, force: true);
            }
            finally
            {
                Volatile.Write(ref _started, 0);
            }
            throw;
        }
    }

    public void Stop(WebServer? web, McpHost? mcp)
    {
        Bootstrap.Shutdown(web, mcp);
        Volatile.Write(ref _started, 0);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (Volatile.Read(ref _started) != 0)
            {
                Bootstrap.Shutdown(null, null, force: true);
                Volatile.Write(ref _started, 0);
            }
        }
        finally
        {
            await _composition.DisposeAsync().ConfigureAwait(false);
        }
    }
}
