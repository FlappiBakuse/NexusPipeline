using System.Net;

namespace NexusPipeline.Services.Update;

/// <summary>更新资源的安全域；策略文件与清单、发布资产使用独立的 URI 校验规则。</summary>
internal enum UpdateResourceKind
{
    Manifest,
    Policy,
    ReleaseAsset,
}

/// <summary>
/// 更新供应链 URI 策略：清单、策略文件、ZIP、SHA 和重定向分别经过对应资源规则。
/// 自定义源默认要求同源 HTTPS；回环 HTTP 仅用于本地测试源。
/// </summary>
internal sealed class UpdateSourcePolicy
{
    private static readonly HashSet<string> DefaultAssetHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com",
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com",
        "raw.githubusercontent.com",
    };

    private readonly bool _isDefaultSource;

    private const string DefaultPolicyHost = "raw.githubusercontent.com";
    private const string DefaultPolicyPath = "/FlappiBakuse/NexusPipeline/main/update-policy.json";

    public Uri SourceUri { get; }

    internal bool IsDefaultSource => _isDefaultSource;

    public UpdateSourcePolicy(string? sourceUrl)
    {
        string source = string.IsNullOrWhiteSpace(sourceUrl) ? UpdateCatalog.DefaultSourceUrl : sourceUrl.Trim();
        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri))
        {
            throw new ArgumentException("更新源地址无效", nameof(sourceUrl));
        }
        SourceUri = uri;
        _isDefaultSource = string.IsNullOrWhiteSpace(sourceUrl);
    }

    public string? ValidateManifestUri(Uri uri)
    {
        string? schemeError = ValidateScheme(uri, allowLoopbackHttp: true);
        if (schemeError is not null)
        {
            return schemeError;
        }
        if (_isDefaultSource)
        {
            return string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase)
                ? null
                : $"GitHub 清单地址主机不受信任：{uri.Host}";
        }
        return IsSameOrigin(SourceUri, uri) ? null : "自定义更新清单必须与更新源同源";
    }

    public string? ValidateAssetUri(Uri uri)
    {
        string? schemeError = ValidateScheme(uri, allowLoopbackHttp: SourceUri.IsLoopback);
        if (schemeError is not null)
        {
            return schemeError;
        }
        if (_isDefaultSource)
        {
            return DefaultAssetHosts.Contains(uri.Host)
                ? null
                : $"GitHub 更新资产地址主机不受信任：{uri.Host}";
        }
        return IsSameOrigin(SourceUri, uri) ? null : "自定义更新资产必须与更新源同源";
    }

    /// <summary>
    /// 校验更新策略授权文件。默认源只允许官方仓库 main 分支的固定路径；
    /// 自定义源只允许同源 HTTPS（回环 HTTP 仅供测试）。
    /// </summary>
    public string? ValidatePolicyUri(Uri uri)
    {
        string? schemeError = ValidateScheme(uri, allowLoopbackHttp: SourceUri.IsLoopback);
        if (schemeError is not null)
        {
            return schemeError;
        }
        if (string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment))
        {
            if (_isDefaultSource)
            {
                if (string.Equals(uri.Host, DefaultPolicyHost, StringComparison.OrdinalIgnoreCase)
                    && EffectivePort(uri) == 443
                    && string.Equals(uri.AbsolutePath, DefaultPolicyPath, StringComparison.Ordinal))
                {
                    return null;
                }
                return "默认更新策略地址必须指向官方仓库 main/update-policy.json";
            }
            if (IsSameOrigin(SourceUri, uri))
            {
                return null;
            }
        }
        return _isDefaultSource
            ? "默认更新策略地址不受信任"
            : "自定义更新策略必须与更新源同源且不带附加参数";
    }

    public string? ValidateRedirectDestination(Uri uri, UpdateResourceKind resourceKind)
    {
        return ValidateResourceUri(uri, resourceKind);
    }

    public bool IsAllowedHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }
        if (_isDefaultSource)
        {
            return DefaultAssetHosts.Contains(host);
        }
        return string.Equals(SourceUri.Host, host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>发送 GET 并手动跟随重定向；每一跳都先经过当前资源策略。</summary>
    public async Task<HttpResponseMessage> GetAsync(
        HttpClient http,
        Uri uri,
        UpdateResourceKind resourceKind,
        string userAgent,
        CancellationToken token,
        Action<HttpRequestMessage>? configureRequest = null)
    {
        Uri current = uri;
        for (int redirect = 0; redirect <= 5; redirect++)
        {
            string? validationError = ValidateResourceUri(current, resourceKind);
            if (validationError is not null)
            {
                throw new InvalidDataException(validationError);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            if (resourceKind is UpdateResourceKind.Manifest or UpdateResourceKind.Policy)
            {
                request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            }
            configureRequest?.Invoke(request);
            HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is
                HttpStatusCode.MultipleChoices
                or HttpStatusCode.MovedPermanently
                or HttpStatusCode.Found
                or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect)
            {
                Uri? next = response.Headers.Location;
                response.Dispose();
                if (next is null)
                {
                    throw new InvalidDataException("更新源重定向缺少目标地址");
                }
                if (!next.IsAbsoluteUri)
                {
                    next = new Uri(current, next);
                }
                if (redirect == 5)
                {
                    throw new InvalidDataException("更新源重定向次数超过上限");
                }
                current = next;
                continue;
            }
            return response;
        }
        throw new InvalidDataException("更新源重定向失败");
    }

    private string? ValidateResourceUri(Uri uri, UpdateResourceKind resourceKind)
    {
        return resourceKind switch
        {
            UpdateResourceKind.Manifest => ValidateManifestUri(uri),
            UpdateResourceKind.Policy => ValidatePolicyUri(uri),
            UpdateResourceKind.ReleaseAsset => ValidateAssetUri(uri),
            _ => "更新资源类型无效",
        };
    }

    private string? ValidateScheme(Uri uri, bool allowLoopbackHttp)
    {
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return null;
        }
        if (allowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && SourceUri.IsLoopback)
        {
            return null;
        }
        return "更新地址必须是 https 地址";
    }

    private static bool IsSameOrigin(Uri left, Uri right)
    {
        return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
            && EffectivePort(left) == EffectivePort(right);
    }

    private static int EffectivePort(Uri uri)
    {
        if (uri.Port > 0)
        {
            return uri.Port;
        }
        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;
    }
}
