using NexusPipeline.Plugin.Abstractions;
using System.Security.Cryptography;

namespace NexusPipeline.TestPlugin;

public sealed class FixtureState
{
    public bool Initialized { get; set; }

    public bool Started { get; set; }

    public bool Stopped { get; set; }

    public bool JobRan { get; set; }
}

public sealed class TestPlugin : INexusPlugin
{
    private IPluginHostContext? _context;
    private IDisposable? _badgeRegistration;

    public async ValueTask InitializeAsync(IPluginHostContext context, CancellationToken cancellationToken)
    {
        _context = context;
        if (context is IPluginHostContextV1_2 v12)
        {
            _badgeRegistration = v12.UserListBadges.Register(new PluginUserListBadgeContribution(
                "fixture-badge",
                10,
                (_, _) => ValueTask.FromResult<PluginUserListBadge?>(new PluginUserListBadge("Fixture 徽章", "blue", "Fixture"))));
        }
        await SetStateAsync(state => state.Initialized = true, cancellationToken).ConfigureAwait(false);
        try
        {
            await context.Secrets.SetAsync("fixture-token", "fixture-secret", cancellationToken).ConfigureAwait(false);
        }
        catch (CryptographicException ex)
        {
            // Some CI test hosts do not load the Windows user profile. The host API remains callable;
            // the production path still propagates the DPAPI failure to the plugin when it is unsupported.
            context.Logger.Warn($"fixture secret unavailable: {ex.Message}");
        }
        await context.Notifications.SendAsync(new PluginNotification("fixture", "managed-code notification"), cancellationToken).ConfigureAwait(false);
        context.Scheduler.Register(
            new PluginJobDefinition("fixture-job", Interval: TimeSpan.FromMilliseconds(50), Timeout: TimeSpan.FromSeconds(1)),
            (_, token) => RunJobAsync(token));
    }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        return SetStateAsync(state => state.Started = true, cancellationToken);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _badgeRegistration?.Dispose();
        _badgeRegistration = null;
        return SetStateAsync(state => state.Stopped = true, cancellationToken);
    }

    private async ValueTask RunJobAsync(CancellationToken cancellationToken)
    {
        await SetStateAsync(state => state.JobRan = true, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SetStateAsync(Action<FixtureState> update, CancellationToken cancellationToken)
    {
        if (_context is null)
        {
            throw new InvalidOperationException("fixture plugin context is not initialized");
        }
        FixtureState state = await _context.Config.ReadAsync<FixtureState>(cancellationToken).ConfigureAwait(false) ?? new FixtureState();
        update(state);
        await _context.Config.WriteAsync(state, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class FailingPlugin : INexusPlugin
{
    public ValueTask InitializeAsync(IPluginHostContext context, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("fixture init failure");
    }

    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
