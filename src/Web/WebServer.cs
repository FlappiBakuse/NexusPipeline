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
        string prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Clear();
        _listener.Prefixes.Add(prefix);
        _cts = new CancellationTokenSource();
        _listener.Start();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        Logger.Info($"Web 服务已启动：{prefix}");
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
            case "fs":
                await ApiFsHandler.Handle(context, method, seg).ConfigureAwait(false);
                return;
            default:
                await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                return;
        }
    }
}
