using System.Net;
using NexusPipeline.Models;
using NexusPipeline.Persistence;

namespace NexusPipeline.Services.Networking;

/// <summary>宿主外部网络请求的代理模式。</summary>
internal enum OutboundHttpTarget
{
    External,
    Loopback,
}

/// <summary>从当前设置构造一次性 HTTP handler；代理密码始终只在进程内解密使用。</summary>
internal sealed record ProxyConfiguration(
    string Mode,
    Uri? ProxyUri,
    string Username,
    string Password)
{
    public static ProxyConfiguration FromSettings(AppSettings settings)
    {
        string mode = (settings.ProxyMode ?? "none").Trim().ToLowerInvariant();
        if (mode is not ("none" or "system" or "http"))
        {
            mode = "none";
        }

        Uri? proxyUri = null;
        if (!string.IsNullOrWhiteSpace(settings.ProxyUrl))
        {
            if (!Uri.TryCreate(settings.ProxyUrl.Trim(), UriKind.Absolute, out Uri? parsed)
                || (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("代理地址必须是 http:// 或 https:// 地址");
            }
            proxyUri = parsed;
        }

        if (mode == "http" && proxyUri is null)
        {
            throw new InvalidDataException("自定义代理模式需要填写 HTTP/HTTPS 代理地址");
        }

        string password = "";
        if (!string.IsNullOrWhiteSpace(settings.ProxyPassword))
        {
            if (!SecretStore.TryDecrypt(settings.ProxyPassword, out string? decryptedPassword))
            {
                throw new InvalidDataException("代理密码无法解密，请重新填写代理密码");
            }
            password = decryptedPassword ?? "";
        }

        return new ProxyConfiguration(
            mode,
            proxyUri,
            (settings.ProxyUsername ?? "").Trim(),
            password);
    }

    public HttpClientHandler CreateHandler(OutboundHttpTarget target, bool allowAutoRedirect)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
        };

        if (target == OutboundHttpTarget.Loopback || Mode == "none")
        {
            handler.UseProxy = false;
            return handler;
        }

        handler.UseProxy = true;
        if (Mode == "http" && ProxyUri is not null)
        {
            var proxy = new WebProxy(ProxyUri)
            {
                BypassProxyOnLocal = true,
            };
            if (!string.IsNullOrWhiteSpace(Username))
            {
                proxy.Credentials = new NetworkCredential(Username, Password);
            }
            handler.Proxy = proxy;
        }
        // system 模式保留 HttpClientHandler 的默认系统代理解析。
        return handler;
    }
}

/// <summary>宿主统一外网 HTTP 出口。每次创建 client 都读取最新设置，保存代理后立即生效。</summary>
internal sealed class OutboundHttpClientProvider
{
    private readonly Func<AppSettings> _settings;

    public OutboundHttpClientProvider(Func<AppSettings> settings)
    {
        _settings = settings;
    }

    public HttpClient CreateClient(
        Uri? destination,
        TimeSpan timeout,
        bool allowAutoRedirect = false)
    {
        OutboundHttpTarget target = destination?.IsLoopback == true
            ? OutboundHttpTarget.Loopback
            : OutboundHttpTarget.External;
        ProxyConfiguration proxy = ProxyConfiguration.FromSettings(_settings());
        HttpClient client = new(proxy.CreateHandler(target, allowAutoRedirect))
        {
            Timeout = timeout,
        };
        return client;
    }

    public HttpClient CreateClient(
        OutboundHttpTarget target,
        TimeSpan timeout,
        bool allowAutoRedirect = false)
    {
        ProxyConfiguration proxy = ProxyConfiguration.FromSettings(_settings());
        return new HttpClient(proxy.CreateHandler(target, allowAutoRedirect))
        {
            Timeout = timeout,
        };
    }
}
