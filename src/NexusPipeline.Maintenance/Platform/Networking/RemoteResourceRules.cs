namespace NexusPipeline.Platform.Networking;

/// <summary>
/// Explicit, product-neutral rules for one remote-resource flow. Product modules
/// construct these values; Platform only evaluates them while making HTTP calls.
/// </summary>
internal sealed class RemoteResourceRules
{
    internal RemoteResourceRules(
        Uri sourceUri,
        IEnumerable<string> allowedSchemes,
        IEnumerable<string> allowedHosts,
        bool sameOriginOnly,
        bool allowLoopbackHttp,
        TimeSpan timeout,
        int maxRedirects = 5,
        bool rejectUserInfo = true,
        bool rejectQuery = false,
        bool rejectFragment = false,
        bool requireHttpsDefaultPort = false,
        IEnumerable<string>? initialAllowedHosts = null,
        bool requireInitialSourceUri = true)
    {
        SourceUri = sourceUri ?? throw new ArgumentNullException(nameof(sourceUri));
        AllowedSchemes = new HashSet<string>(
            allowedSchemes ?? throw new ArgumentNullException(nameof(allowedSchemes)),
            StringComparer.OrdinalIgnoreCase);
        AllowedHosts = new HashSet<string>(
            allowedHosts ?? throw new ArgumentNullException(nameof(allowedHosts)),
            StringComparer.OrdinalIgnoreCase);
        SameOriginOnly = sameOriginOnly;
        AllowLoopbackHttp = allowLoopbackHttp;
        Timeout = timeout > TimeSpan.Zero ? timeout : throw new ArgumentOutOfRangeException(nameof(timeout));
        MaxRedirects = maxRedirects is >= 0 and <= 10
            ? maxRedirects
            : throw new ArgumentOutOfRangeException(nameof(maxRedirects));
        RejectUserInfo = rejectUserInfo;
        RejectQuery = rejectQuery;
        RejectFragment = rejectFragment;
        RequireHttpsDefaultPort = requireHttpsDefaultPort;
        RequireInitialSourceUri = requireInitialSourceUri;
        InitialAllowedHosts = new HashSet<string>(
            initialAllowedHosts ?? AllowedHosts,
            StringComparer.OrdinalIgnoreCase);
    }

    internal Uri SourceUri { get; }

    internal IReadOnlySet<string> AllowedSchemes { get; }

    internal IReadOnlySet<string> AllowedHosts { get; }

    internal bool SameOriginOnly { get; }

    internal bool AllowLoopbackHttp { get; }

    internal TimeSpan Timeout { get; }

    internal int MaxRedirects { get; }

    internal bool RejectUserInfo { get; }

    internal bool RejectQuery { get; }

    internal bool RejectFragment { get; }

    internal bool RequireHttpsDefaultPort { get; }

    internal bool RequireInitialSourceUri { get; }

    internal IReadOnlySet<string> InitialAllowedHosts { get; }

    internal string? Validate(Uri uri)
    {
        string? commonError = ValidateCommon(uri);
        if (commonError is not null)
        {
            return commonError;
        }
        if (SameOriginOnly)
        {
            return IsSameOrigin(SourceUri, uri) ? null : "远程资源地址必须与来源同源";
        }
        return AllowedHosts.Contains(uri.Host)
            ? null
            : $"远程资源地址主机不受信任：{uri.Host}";
    }

    internal string? ValidateInitial(Uri uri)
    {
        if (RequireInitialSourceUri
            && !string.Equals(uri.AbsoluteUri, SourceUri.AbsoluteUri, StringComparison.Ordinal))
        {
            return "远程资源首跳必须与声明来源完全一致";
        }
        string? commonError = ValidateCommon(uri);
        if (commonError is not null)
        {
            return commonError;
        }
        if (SameOriginOnly)
        {
            return IsSameOrigin(SourceUri, uri) ? null : "远程资源地址必须与来源同源";
        }
        return InitialAllowedHosts.Contains(uri.Host)
            ? null
            : $"远程资源首跳主机不受信任：{uri.Host}";
    }

    internal bool IsAllowedHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }
        if (SameOriginOnly)
        {
            return string.Equals(SourceUri.Host, host, StringComparison.OrdinalIgnoreCase);
        }
        return AllowedHosts.Contains(host);
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

    private string? ValidateCommon(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            return "远程资源地址必须是绝对 URI";
        }
        if (!AllowedSchemes.Contains(uri.Scheme)
            && !(AllowLoopbackHttp
                && uri.Scheme == Uri.UriSchemeHttp
                && uri.IsLoopback
                && SourceUri.IsLoopback))
        {
            return "远程资源地址协议不受信任";
        }
        if (RejectUserInfo && !string.IsNullOrEmpty(uri.UserInfo))
        {
            return "远程资源地址不得包含用户信息";
        }
        if (RejectQuery && !string.IsNullOrEmpty(uri.Query))
        {
            return "远程资源地址不得包含查询参数";
        }
        if (RejectFragment && !string.IsNullOrEmpty(uri.Fragment))
        {
            return "远程资源地址不得包含片段";
        }
        if (RequireHttpsDefaultPort
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && EffectivePort(uri) != 443)
        {
            return "远程 HTTPS 资源必须使用 443 端口";
        }
        return null;
    }
}
