using NexusPipeline.Plugin.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

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
    private readonly List<IDisposable> _registrations = new();

    private IPluginHostContext? _context;

    public async ValueTask InitializeAsync(IPluginHostContext context, CancellationToken cancellationToken)
    {
        _context = context;
        if (context is IPluginHostContextV1_2 v12)
        {
            _registrations.Add(v12.UserListBadges.Register(new PluginUserListBadgeContribution(
                "fixture-badge",
                10,
                (_, _) => ValueTask.FromResult<PluginUserListBadge?>(new PluginUserListBadge("Fixture 徽章", "blue", "Fixture")))));
        }
        if (context is IPluginHostContextV1_3 v13)
        {
            RegisterWebApi(v13);
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
        foreach (IDisposable registration in _registrations)
        {
            registration.Dispose();
        }
        _registrations.Clear();
        return SetStateAsync(state => state.Stopped = true, cancellationToken);
    }

    /// <summary>注册宿主 Web API 传输契约所需的固定路由：JSON 回显、二进制请求回显与受限类型响应。</summary>
    private void RegisterWebApi(IPluginHostContextV1_3 context)
    {
        _registrations.Add(context.WebApi.Register(new PluginWebApiRoute(
            "PUT",
            "json/echo",
            (request, _) => ValueTask.FromResult(PluginWebApiResponse.Json(new JsonObject
            {
                ["jsonBody"] = request.JsonBody,
                ["contentType"] = request.ContentType,
                ["contentLength"] = request.ContentLength,
            })))));
        _registrations.Add(context.WebApi.Register(new PluginWebApiRoute(
            "POST",
            "binary/echo",
            async (request, token) =>
            {
                if (request.OpenBodyStream is null)
                {
                    return PluginWebApiResponse.Json(new JsonObject { ["error"] = "missing-body" });
                }
                using var buffer = new MemoryStream();
                using Stream body = await request.OpenBodyStream(token).ConfigureAwait(false);
                await body.CopyToAsync(buffer, token).ConfigureAwait(false);
                return PluginWebApiResponse.Json(new JsonObject
                {
                    ["length"] = buffer.Length,
                    ["text"] = Encoding.UTF8.GetString(buffer.ToArray()),
                    ["contentType"] = request.ContentType,
                });
            })));
        _registrations.Add(context.WebApi.Register(new PluginWebApiRoute(
            "GET",
            "binary/image",
            (_, _) => ValueTask.FromResult(PluginWebApiResponse.Binary(
                new MemoryStream(Encoding.UTF8.GetBytes("fixture-binary-payload")),
                "image/png")))));
        _registrations.Add(context.WebApi.Register(new PluginWebApiRoute(
            "GET",
            "binary/document",
            (_, _) => ValueTask.FromResult(PluginWebApiResponse.Binary(
                new MemoryStream(Encoding.UTF8.GetBytes("<html>fixture</html>")),
                "text/html")))));
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
