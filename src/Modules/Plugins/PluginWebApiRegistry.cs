using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.Modules.Plugins;

internal sealed record PluginWebApiRegistration(
    Guid Token,
    string PluginName,
    PluginWebApiRoute Route);

internal sealed class PluginWebApiRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, PluginWebApiRegistration> _registrations = new();

    public IDisposable Register(string pluginName, PluginWebApiRoute route)
    {
        PluginWebApiValidation.Validate(route);
        Guid token = Guid.NewGuid();
        lock (_sync)
        {
            if (_registrations.Values.Any(item =>
                string.Equals(item.PluginName, pluginName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Route.Method, route.Method, StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeRoute(item.Route.Route), NormalizeRoute(route.Route), StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"插件 Web API 路由重复：{pluginName}/{route.Method} {route.Route}");
            }
            _registrations[token] = new PluginWebApiRegistration(token, pluginName, route with { Method = route.Method.Trim().ToUpperInvariant(), Route = NormalizeRoute(route.Route) });
        }
        return new CallbackDisposable(() => Remove(token));
    }

    public bool TryGet(string pluginName, string method, string route, out PluginWebApiRegistration? registration)
    {
        string normalized = NormalizeRoute(route);
        lock (_sync)
        {
            registration = _registrations.Values.FirstOrDefault(item =>
                string.Equals(item.PluginName, pluginName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Route.Method, method, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Route.Route, normalized, StringComparison.OrdinalIgnoreCase));
            return registration is not null;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _registrations.Clear();
        }
    }

    internal static string NormalizeRoute(string route)
    {
        string normalized = (route ?? "").Trim().Trim('/');
        return normalized.Replace("//", "/", StringComparison.Ordinal);
    }

    private void Remove(Guid token)
    {
        lock (_sync)
        {
            _registrations.Remove(token);
        }
    }
}
