using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.Web;

/// <summary>HTTP 服务骨架：监听、请求分发、静态文件。业务路由见各 ApiXxxHandler（[ApiRoute] 反射扫描注册）。</summary>
internal sealed class WebServer : IDisposable
{
    private delegate Task ApiRouteHandler(HttpListenerContext context, string method, string[] seg, string body);

    /// <summary>请求体大小上限（10MB，防超大 body 内存压力）。</summary>
    private const int MaxRequestBodyBytes = 10 * 1024 * 1024;

    /// <summary>认证失败锁定：连续失败次数与锁定截止时间（按远端 IP，防令牌爆破）。</summary>
    private const int MaxAuthFailsBeforeLock = 5;

    private const int AuthLockSeconds = 60;

    private sealed class AuthFailState
    {
        public int Fails;

        public long LockedUntil;
    }

    private static readonly ConcurrentDictionary<string, AuthFailState> AuthFails = new();

    /// <summary>API 路由表：启动时反射扫描带 [ApiRoute] 的 handler 类/方法注册；新增 API 无需改路由表。</summary>
    private static readonly Dictionary<string, ApiRouteHandler> Routes = BuildRoutes();

    private static Dictionary<string, ApiRouteHandler> BuildRoutes()
    {
        var routes = new Dictionary<string, ApiRouteHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (Type type in typeof(WebServer).Assembly.GetTypes())
        {
            ApiRouteAttribute? classAttr = type.GetCustomAttribute<ApiRouteAttribute>();
            if (classAttr is null)
            {
                continue;
            }
            MethodInfo? handle = type.GetMethod("Handle", BindingFlags.Public | BindingFlags.Static);
            if (handle is not null)
            {
                routes[classAttr.Name] = (ctx, m, seg, b) => InvokeRouteAsync(handle, ctx, m, seg, b);
            }
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                ApiRouteAttribute? methodAttr = method.GetCustomAttribute<ApiRouteAttribute>();
                if (methodAttr is not null)
                {
                    routes[methodAttr.Name] = (ctx, m, seg, b) => InvokeRouteAsync(method, ctx, m, seg, b);
                }
            }
        }
        return routes;
    }

    private static Task InvokeRouteAsync(MethodInfo mi, HttpListenerContext context, string methodName, string[] seg, string body)
    {
        ParameterInfo[] parameters = mi.GetParameters();
        object?[] args = new object?[parameters.Length];
        args[0] = context;
        args[1] = methodName;
        for (int i = 2; i < parameters.Length; i++)
        {
            args[i] = parameters[i].ParameterType == typeof(string[]) ? seg : body;
        }
        object? result = mi.Invoke(null, args);
        return result is Task task ? task : Task.CompletedTask;
    }

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
                if (!IsAllowedOrigin(context, out string? originDetail))
                {
                    Logger.Debug($"[安全] 拒绝跨源请求：{originDetail}");
                    await HttpHelper.WriteJsonAsync(context, new { error = originDetail }, 403).ConfigureAwait(false);
                    return;
                }
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
            // 静态文件路径包含校验（v0.6.3+，纵深防御）：HttpListener 已规范化拒绝 .. 段，此处兜底防止路径逃逸出 wwwroot。
            string fullPath = Path.GetFullPath(filePath);
            string rootFull = Path.GetFullPath(AppPaths.WwwRootDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                return;
            }
            HttpHelper.ServeFile(context, fullPath);
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

    /// <summary>跨站请求防护（v0.6.4+）：带 Origin 头的浏览器请求必须来自合法源（回环或本机局域网地址、且与请求 Host 端口一致），
    /// 阻止任意网页触发的 CSRF 简单请求与 DNS rebinding；无 Origin 的非浏览器请求（CLI/curl）不受限——它们无法自动携带认证凭证。</summary>
    private static bool IsAllowedOrigin(HttpListenerContext context, out string? detail)
    {
        string? origin = context.Request.Headers["Origin"];
        if (string.IsNullOrEmpty(origin))
        {
            detail = null;
            return true;
        }
        string? host = context.Request.Headers["Host"];
        if (string.IsNullOrEmpty(host))
        {
            detail = "缺少 Host 头";
            return false;
        }
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
        {
            detail = $"Origin 非法（{origin}）";
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttp)
        {
            detail = $"Origin 协议非法（{uri.Scheme}）";
            return false;
        }
        bool hostIsLoopback = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uri.Host, out IPAddress? ip) && IPAddress.IsLoopback(ip);
        bool hostIsLan = !hostIsLoopback && NetInfo.ListLanAddresses().Any(address => string.Equals(address, uri.Host, StringComparison.OrdinalIgnoreCase));
        if (!hostIsLoopback && !hostIsLan)
        {
            detail = $"Origin 主机非法（{uri.Host}）";
            return false;
        }
        if (!string.Equals(uri.Authority, host, StringComparison.OrdinalIgnoreCase))
        {
            detail = $"Origin 与 Host 不一致（{origin} vs {host}）";
            return false;
        }
        detail = null;
        return true;
    }

    /// <summary>远程访问认证：未开启远程或本地请求（127.0.0.1/::1）豁免；远程请求需 Bearer 访问令牌；
    /// 连续失败达到阈值后按远端 IP 锁定一段时间（防爆破）。返回是否放行，拒绝时输出判定详情。</summary>
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
        string ip = remote.ToString();
        long now = DateTime.UtcNow.Ticks;
        if (AuthFails.TryGetValue(ip, out AuthFailState? state) && state.LockedUntil > now)
        {
            detail = $"失败次数过多，已锁定（剩余 {TimeSpan.FromTicks(state.LockedUntil - now).TotalSeconds:F0} 秒）";
            return false;
        }
        string? token = null;
        if (SecretStore.TryDecrypt(settings.AccessToken, out string? plain) && !string.IsNullOrWhiteSpace(plain))
        {
            token = plain;
        }
        string? auth = context.Request.Headers["Authorization"];
        bool ok = token is not null && auth is not null && auth.Equals("Bearer " + token, StringComparison.Ordinal);
        if (ok)
        {
            AuthFails.TryRemove(ip, out _);
            detail = $"远程请求 {remote}，令牌匹配";
            return true;
        }
        AuthFailState current = AuthFails.GetOrAdd(ip, _ => new AuthFailState());
        current.Fails++;
        if (current.Fails >= MaxAuthFailsBeforeLock)
        {
            current.LockedUntil = now + TimeSpan.FromSeconds(AuthLockSeconds).Ticks;
            current.Fails = 0;
            detail = $"远程请求 {remote}，令牌不匹配（已触发锁定 {AuthLockSeconds} 秒）";
        }
        else
        {
            detail = $"远程请求 {remote}，令牌不匹配/缺失（失败 {current.Fails}/{MaxAuthFailsBeforeLock}）";
        }
        return false;
    }

    private static async Task HandleApiAsync(HttpListenerContext context, string method, string path, CancellationToken token)
    {
        string body = "";
        if (context.Request.HasEntityBody)
        {
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
            var buffer = new char[81920];
            var text = new StringBuilder();
            int total = 0;
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxRequestBodyBytes)
                {
                    await HttpHelper.WriteJsonAsync(context, new { error = "请求体过大（上限 10MB）" }, 413).ConfigureAwait(false);
                    return;
                }
                text.Append(buffer, 0, read);
            }
            body = text.ToString();
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
        if (Routes.TryGetValue(resource, out ApiRouteHandler? handler))
        {
            await handler(context, method, seg, body).ConfigureAwait(false);
            return;
        }
        await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
    }
}
