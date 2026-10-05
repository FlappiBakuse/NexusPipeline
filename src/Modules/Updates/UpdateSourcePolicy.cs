using NexusPipeline.Platform.Networking;

namespace NexusPipeline.Modules.Updates;

/// <summary>资源类别由 Updates 定义；Platform 只执行显式的通用远程规则。</summary>
internal enum UpdateResourceKind
{
    Manifest,
    Policy,
    ReleaseAsset,
}

/// <summary>Host 更新模块的来源与资源策略。</summary>
internal sealed class UpdateSourcePolicy
{
    private readonly RemoteResourcePolicy _manifest;
    private readonly RemoteResourcePolicy _policy;
    private readonly RemoteResourcePolicy _asset;
    private readonly bool _isDefaultSource;

    public Uri SourceUri { get; }

    internal bool IsDefaultSource => _isDefaultSource;

    public UpdateSourcePolicy(string? sourceUrl)
    {
        string source = string.IsNullOrWhiteSpace(sourceUrl)
            ? UpdateRemoteResourceRules.DefaultSourceUrl
            : sourceUrl.Trim();
        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri))
        {
            throw new ArgumentException("更新源地址无效", nameof(sourceUrl));
        }

        SourceUri = uri;
        _isDefaultSource = string.IsNullOrWhiteSpace(sourceUrl);
        _manifest = new RemoteResourcePolicy(UpdateRemoteResourceRules.Create(uri, _isDefaultSource, UpdateResourceKind.Manifest));
        _policy = new RemoteResourcePolicy(UpdateRemoteResourceRules.Create(uri, _isDefaultSource, UpdateResourceKind.Policy));
        _asset = new RemoteResourcePolicy(UpdateRemoteResourceRules.Create(uri, _isDefaultSource, UpdateResourceKind.ReleaseAsset));
    }

    public string? ValidateManifestUri(Uri uri) => _manifest.ValidateUri(uri);

    public string? ValidateAssetUri(Uri uri) => _asset.ValidateUri(uri);

    public string? ValidateRedirectDestination(Uri uri, UpdateResourceKind resourceKind)
        => PolicyFor(resourceKind).ValidateRedirectDestination(uri);

    public bool IsAllowedHost(string host) => _asset.IsAllowedHost(host);

    public Task<HttpResponseMessage> GetAsync(
        HttpClient http,
        Uri uri,
        UpdateResourceKind resourceKind,
        string userAgent,
        CancellationToken token,
        Action<HttpRequestMessage>? configureRequest = null)
    {
        RemoteResourcePolicy policy = PolicyFor(resourceKind);
        Action<HttpRequestMessage> configure = request =>
        {
            if (resourceKind is UpdateResourceKind.Manifest or UpdateResourceKind.Policy)
            {
                request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            }
            configureRequest?.Invoke(request);
        };
        return policy.GetAsync(http, uri, userAgent, token, configure);
    }

    private RemoteResourcePolicy PolicyFor(UpdateResourceKind resourceKind)
        => resourceKind switch
        {
            UpdateResourceKind.Manifest => _manifest,
            UpdateResourceKind.Policy => _policy,
            UpdateResourceKind.ReleaseAsset => _asset,
            _ => throw new ArgumentOutOfRangeException(nameof(resourceKind), resourceKind, null),
        };
}
