using System.Collections.Specialized;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Plugins;
using NexusPipeline.Utilities;

namespace NexusPipeline.Web;

/// <summary>插件自有 Web API 代理。插件只能处理自己注册的 /api/plugin-api/{plugin}/ 路由。</summary>
[ApiRoute("plugin-api")]
internal static class ApiPluginWebApiHandler
{
    private static readonly TimeSpan HandlerTimeout = TimeSpan.FromSeconds(30);
    private const int MaxResponseBytes = 2 * 1024 * 1024;

    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body)
    {
        if (seg.Length < 3)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }

        string pluginName = Uri.UnescapeDataString(seg[1]);
        string route = string.Join("/", seg.Skip(2).Select(Uri.UnescapeDataString));
        PluginManager plugins = RuntimeContext.Instance.Plugins;
        if (!plugins.TryGetWebApi(pluginName, method, route, out PluginWebApiRegistration? registration)
            || registration is null)
        {
            await HttpHelper.WriteJsonAsync(
                context,
                new { ok = false, code = "plugin_api_not_found", error = "插件 Web API 路由不存在或插件未启用" },
                404).ConfigureAwait(false);
            return;
        }

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        NameValueCollection queryString = context.Request.QueryString;
        foreach (string? key in queryString.AllKeys)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                query[key] = queryString[key] ?? "";
            }
        }

        PluginWebApiResponse response;
        try
        {
            using var timeout = new CancellationTokenSource(HandlerTimeout);
            var request = new PluginWebApiRequest(
                method,
                registration.Route.Route,
                query,
                string.IsNullOrEmpty(body) ? null : body);
            response = await registration.Route.Handler(request, timeout.Token)
                .AsTask()
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Logger.Warn($"[插件:{registration.PluginName}] Web API 处理超时：{method} {registration.Route.Route}");
            await PluginErrorAsync(context, "插件 Web API 处理超时").ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件:{registration.PluginName}] Web API 处理失败：{method} {registration.Route.Route}：{ex.Message}");
            await PluginErrorAsync(context, "插件 Web API 处理失败").ConfigureAwait(false);
            return;
        }

        if (response is null || response.StatusCode is < 200 or > 599)
        {
            Logger.Warn($"[插件:{registration.PluginName}] Web API 返回状态码无效：{response?.StatusCode}");
            await PluginErrorAsync(context, "插件 Web API 返回无效响应").ConfigureAwait(false);
            return;
        }
        if (response.StatusCode == 204 && response.JsonBody is not null)
        {
            await PluginErrorAsync(context, "插件 Web API 的 204 响应不能包含内容").ConfigureAwait(false);
            return;
        }
        if (response.JsonBody is not null)
        {
            try
            {
                byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(response.JsonBody, JsonOpts.Web);
                if (serialized.Length > MaxResponseBytes)
                {
                    await PluginErrorAsync(context, "插件 Web API 响应过大").ConfigureAwait(false);
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[插件:{registration.PluginName}] Web API 响应序列化失败：{ex.Message}");
                await PluginErrorAsync(context, "插件 Web API 响应无效").ConfigureAwait(false);
                return;
            }
        }

        if (response.StatusCode == 204)
        {
            await HttpHelper.NoContentAsync(context).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, response.JsonBody ?? new JsonObject(), response.StatusCode).ConfigureAwait(false);
    }

    private static Task PluginErrorAsync(HttpListenerContext context, string message) =>
        HttpHelper.WriteJsonAsync(context, new { ok = false, code = "plugin_error", error = message }, 500);
}
