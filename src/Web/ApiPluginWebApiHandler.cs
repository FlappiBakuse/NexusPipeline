using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Plugins;
using NexusPipeline.Utilities;

namespace NexusPipeline.Web;

/// <summary>
/// 插件自有 Web API 代理。插件只能处理自己注册的 /api/plugin-api/{plugin}/ 路由。
/// 请求体按原始字节读取：JSON 类型投影为 JsonBody，其余类型通过 OpenBodyStream 暴露；
/// 响应支持 JSON 与受限 Content-Type 的二进制流。
/// </summary>
[ApiRoute("plugin-api", BodyMode = ApiBodyMode.Raw, MaxBodyBytes = MaxRequestBytes)]
internal static class ApiPluginWebApiHandler
{
    /// <summary>插件 Web API 请求体上限；插件业务配额应低于该值。</summary>
    internal const int MaxRequestBytes = 16 * 1024 * 1024;

    private static readonly TimeSpan HandlerTimeout = TimeSpan.FromSeconds(30);
    private const int MaxJsonResponseBytes = 2 * 1024 * 1024;
    private const int MaxBinaryResponseBytes = 16 * 1024 * 1024;

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
            await HttpHelper.ErrorAsync(context, "plugin_api_not_found", 404).ConfigureAwait(false);
            return;
        }

        byte[] requestBody;
        try
        {
            requestBody = await ReadRequestBodyAsync(context).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            await HttpHelper.ErrorAsync(context, "request_too_large", 413, new { maxMb = MaxRequestBytes / (1024 * 1024) }).ConfigureAwait(false);
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

        string contentType = PluginWebApiContentTypes.Normalize(context.Request.ContentType);
        PluginWebApiResponse response;
        try
        {
            using var timeout = new CancellationTokenSource(HandlerTimeout);
            var request = new PluginWebApiRequest(
                method,
                registration.Route.Route,
                query,
                IsJsonContentType(contentType) && requestBody.Length > 0
                    ? Encoding.UTF8.GetString(requestBody)
                    : null)
            {
                ContentType = contentType,
                ContentLength = requestBody.Length,
                OpenBodyStream = requestBody.Length > 0
                    ? _ => ValueTask.FromResult<Stream>(new MemoryStream(requestBody, writable: false))
                    : null,
            };
            response = await registration.Route.Handler(request, timeout.Token)
                .AsTask()
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Logger.Warn($"[插件:{registration.PluginName}] Web API 处理超时：{method} {registration.Route.Route}");
            await PluginErrorAsync(context).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件:{registration.PluginName}] Web API 处理失败：{method} {registration.Route.Route}：{ex.Message}");
            await PluginErrorAsync(context).ConfigureAwait(false);
            return;
        }

        if (response is null || response.StatusCode is < 200 or > 599)
        {
            Logger.Warn($"[插件:{registration.PluginName}] Web API 返回状态码无效：{response?.StatusCode}");
            await PluginErrorAsync(context).ConfigureAwait(false);
            return;
        }
        if (response.BinaryBody is not null)
        {
            await WriteBinaryResponseAsync(context, registration, response).ConfigureAwait(false);
            return;
        }
        if (response.StatusCode == 204 && response.JsonBody is not null)
        {
            await PluginErrorAsync(context).ConfigureAwait(false);
            return;
        }
        if (response.JsonBody is not null)
        {
            try
            {
                byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(response.JsonBody, JsonOpts.Web);
                if (serialized.Length > MaxJsonResponseBytes)
                {
                    await PluginErrorAsync(context).ConfigureAwait(false);
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[插件:{registration.PluginName}] Web API 响应序列化失败：{ex.Message}");
                await PluginErrorAsync(context).ConfigureAwait(false);
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

    private static async Task<byte[]> ReadRequestBodyAsync(HttpListenerContext context)
    {
        if (!context.Request.HasEntityBody)
        {
            return Array.Empty<byte>();
        }
        long declared = context.Request.ContentLength64;
        if (declared > MaxRequestBytes)
        {
            throw new InvalidDataException("插件 Web API 请求体过大");
        }
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[64 * 1024];
        while (true)
        {
            int read = await context.Request.InputStream.ReadAsync(chunk).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > MaxRequestBytes)
            {
                throw new InvalidDataException("插件 Web API 请求体过大");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static async Task WriteBinaryResponseAsync(
        HttpListenerContext context,
        PluginWebApiRegistration registration,
        PluginWebApiResponse response)
    {
        Stream content = response.BinaryBody!;
        try
        {
            if (!PluginWebApiContentTypes.IsAllowedBinary(response.ContentType))
            {
                Logger.Warn($"[插件:{registration.PluginName}] Web API 二进制响应类型不被允许：{response.ContentType}");
                await PluginErrorAsync(context).ConfigureAwait(false);
                return;
            }
            string mime = PluginWebApiContentTypes.Normalize(response.ContentType);
            long length = response.ContentLength;
            if (length < 0 && content.CanSeek)
            {
                length = content.Length - content.Position;
            }
            if (length > MaxBinaryResponseBytes)
            {
                await PluginErrorAsync(context).ConfigureAwait(false);
                return;
            }
            if (response.StatusCode == 204)
            {
                await PluginErrorAsync(context).ConfigureAwait(false);
                return;
            }
            context.Response.StatusCode = response.StatusCode;
            context.Response.ContentType = mime;
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (length >= 0)
            {
                context.Response.ContentLength64 = length;
            }
            byte[] chunk = new byte[64 * 1024];
            long written = 0;
            while (true)
            {
                int read = await content.ReadAsync(chunk).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                written += read;
                if (written > MaxBinaryResponseBytes)
                {
                    throw new InvalidDataException("插件 Web API 响应过大");
                }
                await context.Response.OutputStream.WriteAsync(chunk.AsMemory(0, read)).ConfigureAwait(false);
            }
            context.Response.OutputStream.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpListenerException)
        {
            Logger.Warn($"[插件:{registration.PluginName}] Web API 二进制响应写入失败：{ex.Message}");
        }
        finally
        {
            content.Dispose();
        }
    }

    private static bool IsJsonContentType(string contentType) =>
        contentType.Length == 0
        || contentType is "application/json" or "text/json" or "application/problem+json"
        || contentType.EndsWith("+json", StringComparison.Ordinal);

    private static Task PluginErrorAsync(HttpListenerContext context) =>
        HttpHelper.ErrorAsync(context, "plugin_error", 500);
}
