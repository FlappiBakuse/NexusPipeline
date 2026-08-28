using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
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

    private sealed record ApiRouteDefinition(
        ApiRouteHandler Handler,
        ApiBodyMode BodyMode,
        int MaxBodyBytes);

    /// <summary>请求体大小上限（10MB，防超大 body 内存压力）。</summary>
    private const int MaxRequestBodyBytes = 10 * 1024 * 1024;

    /// <summary>认证失败锁定：连续失败次数与锁定截止时间（按远端 IP，防令牌爆破）。</summary>
    private const int MaxAuthFailsBeforeLock = 5;

    private const int AuthLockSeconds = 60;

    /// <summary>：无活动条目保留宽限（超过即清理，防远端 IP 字典无限增长）。</summary>
    private const int AuthFailIdlePruneSeconds = 600;

    private sealed class AuthFailState
    {
        public int Fails;

        public long LockedUntil;

        public long LastActive;
    }

    private static readonly ConcurrentDictionary<string, AuthFailState> AuthFails = new();

    private static long _lastAuthPruneTicks;

    /// <summary>：每 60 秒清理一次「超过宽限无活动」的远端认证失败条目（锁定中条目保留至锁定过期）。
    /// 仅在远程访问开启且收到请求时触发，避免后台空闲轮询。</summary>
    private static void PruneAuthFails(long now)
    {
        if (now - Interlocked.Read(ref _lastAuthPruneTicks) < TimeSpan.FromSeconds(60).Ticks)
        {
            return;
        }
        Interlocked.Exchange(ref _lastAuthPruneTicks, now);
        foreach (KeyValuePair<string, AuthFailState> pair in AuthFails)
        {
            if (now - pair.Value.LastActive > TimeSpan.FromSeconds(AuthFailIdlePruneSeconds).Ticks)
            {
                AuthFails.TryRemove(pair.Key, out _);
            }
        }
    }

    /// <summary>API 路由表：启动时反射扫描带 [ApiRoute] 的 handler 类/方法注册；新增 API 无需改路由表。</summary>
    private static readonly Dictionary<string, ApiRouteDefinition> Routes = BuildRoutes();

    private static Dictionary<string, ApiRouteDefinition> BuildRoutes()
    {
        var routes = new Dictionary<string, ApiRouteDefinition>(StringComparer.OrdinalIgnoreCase);
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
                routes[classAttr.Name] = new ApiRouteDefinition(
                    (ctx, m, seg, b) => InvokeRouteAsync(handle, ctx, m, seg, b),
                    classAttr.BodyMode,
                    classAttr.MaxBodyBytes);
            }
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                ApiRouteAttribute? methodAttr = method.GetCustomAttribute<ApiRouteAttribute>();
                if (methodAttr is not null)
                {
                    routes[methodAttr.Name] = new ApiRouteDefinition(
                        (ctx, m, seg, b) => InvokeRouteAsync(method, ctx, m, seg, b),
                        methodAttr.BodyMode,
                        methodAttr.MaxBodyBytes);
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

    private WebServerOptions _options = new(true, false);

    private static volatile bool RemoteAccessBound;

    public int Port => _port;

    public bool ServesWebUi => _options.ServeWebUi;

    public bool AllowsRemoteAccess => _options.AllowRemoteAccess;

    /// <summary>
    /// 当前已启动的 Web 服务实例：托盘「打开管理页面」等需用实际监听端口
    /// （设置页改端口未重启 / 启动时端口冲突自动 +1 时与 Settings.WebPort 不一致）。
    /// </summary>
    public static WebServer? Current { get; private set; }

    public void Start(int port, WebServerOptions? options = null)
    {
        _port = port;
        Current = this;
        _options = options ?? WebServerOptions.FromSettings(
            RuntimeContext.Instance.Settings.LightweightMode,
            RuntimeContext.Instance.Settings.AllowRemoteAccess);
        bool remote = _options.AllowRemoteAccess;
        RemoteAccessBound = remote;
        // 远程访问绑定 http.sys 强通配符 +（所有接口）；0.0.0.0 不是合法前缀主机（绑定必失败）。
        string prefix = remote ? $"http://+:{port}/" : $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Clear();
        _listener.Prefixes.Add(prefix);
        _cts = new CancellationTokenSource();
        try
        {
            _listener.Start();
            try
            {
                JsonUtil.WriteAtomic(AppPaths.WebPortPath, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 写入 Web 实际端口标记失败，将依靠端口探测复用服务：{ex.Message}");
            }
        }
        catch
        {
            RemoteAccessBound = false;
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
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
        RemoteAccessBound = false;
        try
        {
            _cts?.Cancel();
            _listener.Stop();
        }
        catch
        {
        }
        try
        {
            if (File.Exists(AppPaths.WebPortPath))
            {
                File.Delete(AppPaths.WebPortPath);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 清理 Web 实际端口标记失败：{ex.Message}");
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
            catch (Exception ex)
            {
                // ：正常停止（_cts.Cancel → Stop 已先调用）时 token 已取消，属预期路径；
                // 其余异常（如 http.sys 故障）时 Web 服务静默死亡不可接受，必须记录。
                if (!token.IsCancellationRequested)
                {
                    Logger.Error($"[错误] HTTP 监听循环异常退出，Web 服务不可用：{ex.Message}");
                }
                return;
            }
            _ = Task.Run(() => HandleAsync(context, token));
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken token)
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
                if (_options.ServeWebUi)
                {
                    HttpHelper.ServeFile(context, Path.Combine(AppPaths.WwwRootDir, "index.html"));
                }
                else
                {
                    await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                }
                return;
            }
            if (path.StartsWith("/plugin-assets/", StringComparison.OrdinalIgnoreCase))
            {
                if (!_options.ServeWebUi)
                {
                    await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                    return;
                }
                if (method is not ("GET" or "HEAD"))
                {
                    await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
                    return;
                }
                string[] assetSegments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (assetSegments.Length < 3)
                {
                    await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                    return;
                }
                string pluginName = Uri.UnescapeDataString(assetSegments[1]);
                string relativePath = string.Join(
                    "/",
                    assetSegments.Skip(2).Select(Uri.UnescapeDataString));
                if (!RuntimeContext.Instance.Plugins.TryResolveFrontendAsset(
                        pluginName,
                        relativePath,
                        out string? assetPath)
                    || assetPath is null)
                {
                    await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                    return;
                }
                HttpHelper.ServeFile(context, assetPath);
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
            if (!_options.ServeWebUi)
            {
                await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                return;
            }
            string filePath = Path.Combine(AppPaths.WwwRootDir, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            // 静态文件路径包含校验（，纵深防御）：HttpListener 已规范化拒绝 .. 段，此处兜底防止路径逃逸出 wwwroot。
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

    /// <summary>跨站请求防护：带 Origin 头的浏览器请求必须来自合法源（回环或本机局域网地址、且与请求 Host 端口一致），
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
        // 监听器是否绑定通配符是启动时决定的；即使运行中关闭设置但未重启，也必须继续保护远程请求。
        if (!RemoteAccessBound)
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
        PruneAuthFails(now);
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
        // （P13）：令牌比较改常量时间（此前字符串 Ordinal 比较非常量时间，可被时序侧信道探测）。
        bool ok = token is not null && auth is not null && TokenEquals(auth, "Bearer " + token);
        if (ok)
        {
            AuthFails.TryRemove(ip, out _);
            detail = $"远程请求 {remote}，令牌匹配";
            return true;
        }
        AuthFailState current = AuthFails.GetOrAdd(ip, _ => new AuthFailState());
        current.LastActive = now;
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

    /// <summary>常量时间令牌比较（P13）：FixedTimeEquals 要求等长字节，先比较长度再定长比较。</summary>
    private static bool TokenEquals(string left, string right)
    {
        byte[] a = Encoding.UTF8.GetBytes(left);
        byte[] b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static async Task HandleApiAsync(HttpListenerContext context, string method, string path, CancellationToken token)
    {
        string[] segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        ApiRouteDefinition? route = segments.Length >= 2
            && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            && Routes.TryGetValue(segments[1], out ApiRouteDefinition? resolved)
            ? resolved
            : null;
        string body = "";
        if (context.Request.HasEntityBody && route?.BodyMode != ApiBodyMode.Raw)
        {
            int maxBodyBytes = route is not null && route.MaxBodyBytes > 0
                ? route.MaxBodyBytes
                : MaxRequestBodyBytes;
            if (context.Request.ContentLength64 > maxBodyBytes)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = $"请求体过大（上限 {maxBodyBytes / (1024 * 1024)}MB）" }, 413).ConfigureAwait(false);
                return;
            }
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
            var buffer = new char[81920];
            var text = new StringBuilder();
            int total = 0;
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBodyBytes)
                {
                    await HttpHelper.WriteJsonAsync(context, new { error = $"请求体过大（上限 {maxBodyBytes / (1024 * 1024)}MB）" }, 413).ConfigureAwait(false);
                    return;
                }
                text.Append(buffer, 0, read);
            }
            body = text.ToString();
        }

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
        if (Routes.TryGetValue(resource, out ApiRouteDefinition? route))
        {
            await route.Handler(context, method, seg, body).ConfigureAwait(false);
            return;
        }
        await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
    }
}
