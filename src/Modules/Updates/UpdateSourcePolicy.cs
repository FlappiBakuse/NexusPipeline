using NexusPipeline.Platform.Networking;

namespace NexusPipeline.Modules.Updates;

/// <summary>更新模块对 Platform 外部资源策略的现役类型适配；实际策略实现归 Platform 所有。</summary>
internal enum UpdateResourceKind
{
    Manifest,
    Policy,
    ReleaseAsset,
}

/// <summary>保留 Updates 既有调用合同，同时避免 Platform 反向依赖 Updates。</summary>
internal sealed class UpdateSourcePolicy
{
    private readonly RemoteResourcePolicy _inner;

    public Uri SourceUri => _inner.SourceUri;

    internal bool IsDefaultSource => _inner.IsDefaultSource;

    public UpdateSourcePolicy(string? sourceUrl)
    {
        _inner = new RemoteResourcePolicy(sourceUrl);
    }

    public string? ValidateManifestUri(Uri uri) => _inner.ValidateManifestUri(uri);

    public string? ValidateAssetUri(Uri uri) => _inner.ValidateAssetUri(uri);

    public string? ValidatePolicyUri(Uri uri) => _inner.ValidatePolicyUri(uri);

    public string? ValidateRedirectDestination(Uri uri, UpdateResourceKind resourceKind) =>
        _inner.ValidateRedirectDestination(uri, resourceKind switch
        {
            UpdateResourceKind.Manifest => RemoteResourceKind.ManifestResource,
            UpdateResourceKind.Policy => RemoteResourceKind.PolicyResource,
            UpdateResourceKind.ReleaseAsset => RemoteResourceKind.ReleaseAssetResource,
            _ => throw new ArgumentOutOfRangeException(nameof(resourceKind), resourceKind, null),
        });

    public bool IsAllowedHost(string host) => _inner.IsAllowedHost(host);

    public Task<HttpResponseMessage> GetAsync(
        HttpClient http,
        Uri uri,
        UpdateResourceKind resourceKind,
        string userAgent,
        CancellationToken token,
        Action<HttpRequestMessage>? configureRequest = null) =>
        _inner.GetAsync(
            http,
            uri,
            resourceKind switch
            {
                UpdateResourceKind.Manifest => RemoteResourceKind.ManifestResource,
                UpdateResourceKind.Policy => RemoteResourceKind.PolicyResource,
                UpdateResourceKind.ReleaseAsset => RemoteResourceKind.ReleaseAssetResource,
                _ => throw new ArgumentOutOfRangeException(nameof(resourceKind), resourceKind, null),
            },
            userAgent,
            token,
            configureRequest);
}
