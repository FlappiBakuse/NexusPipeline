using System.Net;
using System.Text;

namespace NexusPipeline.Web;

/// <summary>HTTP 服务骨架：监听、请求分发、静态文件。业务路由见各 ApiXxxHandler。</summary>
internal sealed class WebServer : IDisposable
{
    private readonly HttpListener _listener = new();

    private CancellationTokenSource? _cts;

    private Task? _loop;

    private int _port;

    public int Port => _port;

    public void Start(int port)
    {
        _port = port;
        bool remote = RuntimeContext.Instance.Settings.AllowRemoteAccess;
        // 远程访问绑定 http.sys 强通配符 +（所有接口）；0.0.0.0 不是合法前缀主机（绑定必失败）。
        string prefix = remote ? $"http://+:{port}/" : $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Clear();
        _listener.Prefixes.Add(prefix);
        _cts = new CancellationTokenSource();
        try
        {
            _listener.Start();
        }
        catch
        {
            // HttpListener.Start 失败后实例不可复用（再次访问抛 ObjectDisposedException），立即关闭，由调用方重建。
            try
            {
                _listener.Close();
            }
            catch
            {
            }
            throw;
        }
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        Logger.Info($"Web 服务已启动：{prefix}（远程访问：{(remote ? "开（需访问令牌）" : "关（仅本地）")}）");
        if (remote)
        {
            List<string> addresses = NetInfo.ListLanAddresses();
            if (addresses.Count > 0)
            {
                foreach (string address in addresses)
                {
                    Logger.Info($"局域网访问地址：http://{address}:{port}/（需访问令牌；localhost 与 0.0.0.0 仅代表本机，无法供其他设备访问）");
                }
            }
            else
            {
                Logger.Info("[提示] 未检测到局域网地址（请检查网络连接；其他设备访问需使用本机局域网 IP）。");
            }
        }
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener.Stop();
        }
        catch
        {
        }
        Logger.Info("Web 服务已停止。");
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(context, token));
        }
    }

    private static async Task HandleAsync(HttpListenerContext context, CancellationToken token)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";
            string method = context.Request.HttpMethod;
            if (!(method == "GET" && path == "/api/status"))
            {
                Logger.Debug($"[Web] {method} {path}");
            }
            if (path == "/" || path == "/index.html")
            {
                HttpHelper.ServeFile(context, Path.Combine(AppPaths.WwwRootDir, "index.html"));
                return;
            }
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                if (!AuthorizeRequest(context, out string? authDetail))
                {
                    Logger.Debug($"[认证] 拒绝远程请求：{authDetail}");
                    context.Response.StatusCode = 401;
                    context.Response.Headers["X-Nexus-Auth"] = "required";
                    await HttpHelper.WriteJsonAsync(context, new { error = "需要访问令牌（请求头 Authorization: Bearer <token>）" }, 401).ConfigureAwait(false);
                    return;
                }
                await HandleApiAsync(context, method, path, token).ConfigureAwait(false);
                return;
            }
            string filePath = Path.Combine(AppPaths.WwwRootDir, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            HttpHelper.ServeFile(context, filePath);
        }
        catch (Exception ex)
        {
            Logger.Error($"[Web] 请求处理异常：{ex.Message}");
            try
            {
                context.Response.StatusCode = 500;
                await HttpHelper.WriteJsonAsync(context, new { error = ex.Message }).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    /// <summary>远程访问认证：未开启远程或本地请求（127.0.0.1/::1）豁免；远程请求需 Bearer 访问令牌。返回是否放行，拒绝时输出判定详情。</summary>
    private static bool AuthorizeRequest(HttpListenerContext context, out string? detail)
    {
        AppSettings settings = RuntimeContext.Instance.Settings;
        if (!settings.AllowRemoteAccess)
        {
            detail = "未开启远程访问";
            return true;
        }
        var remote = context.Request.RemoteEndPoint?.Address;
        if (remote is null || IPAddress.IsLoopback(remote))
        {
            detail = $"本地请求豁免（{remote}）";
            return true;
        }
        string? token = null;
        if (SecretStore.TryDecrypt(settings.AccessToken, out string? plain) && !string.IsNullOrWhiteSpace(plain))
        {
            token = plain;
        }
        string? auth = context.Request.Headers["Authorization"];
        bool ok = token is not null && auth is not null && auth.Equals("Bearer " + token, StringComparison.Ordinal);
        detail = $"远程请求 {remote}，令牌{(ok ? "匹配" : "不匹配/缺失")}";
        return ok;
    }

    private static async Task HandleApiAsync(HttpListenerContext context, string method, string path, CancellationToken token)
    {
        string body = "";
        if (context.Request.HasEntityBody)
        {
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        string[] segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }

        switch (segments[0].ToLowerInvariant())
        {
            case "api":
                await RouteApiAsync(context, method, segments.Skip(1).ToArray(), body).ConfigureAwait(false);
                break;
            default:
                await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                break;
        }
    }

    private static async Task RouteApiAsync(HttpListenerContext context, string method, string[] seg, string body)
    {
        if (seg.Length == 0)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        string resource = seg[0].ToLowerInvariant();
        switch (resource)
        {
            case "status":
                await ApiStatusHandler.Handle(context, method).ConfigureAwait(false);
                return;
            case "scripts":
                await ApiScriptsHandler.Handle(context, method, seg, body).ConfigureAwait(false);
                return;
            case "queues":
                await ApiQueuesHandler.Handle(context, method, seg, body).ConfigureAwait(false);
                return;
            case "dispatch":
                await ApiDispatchHandler.Handle(context, method, seg, body).ConfigureAwait(false);
                return;
            case "cancel":
                await ApiDispatchHandler.HandleCancel(context, method, body).ConfigureAwait(false);
                return;
            case "history":
                await ApiHistoryHandler.Handle(context, method, seg).ConfigureAwait(false);
                return;
            case "settings":
                await ApiSettingsHandler.Handle(context, method, seg, body).ConfigureAwait(false);
                return;
            case "plugins":
                await ApiPluginsHandler.Handle(context, method, seg).ConfigureAwait(false);
                return;
            case "logs":
                await ApiLogsHandler.Handle(context, method).ConfigureAwait(false);
                return;
            case "limits":
                await ApiLimitsHandler.Handle(context, method).ConfigureAwait(false);
                return;
            case "fs":
                await ApiFsHandler.Handle(context, method, seg).ConfigureAwait(false);
                return;
            default:
                await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                return;
        }
    }
}
