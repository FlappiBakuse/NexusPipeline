using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services.Networking;
using NexusPipeline.Services.Update;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

/// <summary>官方插件 catalog 缓存、商店状态合并和生命周期操作编排。</summary>
internal sealed partial class PluginRepositoryService
{
    private static readonly TimeSpan ReadmeCacheTtl = TimeSpan.FromMinutes(5);
    private const long MaxReadmeBytes = 256L * 1024;
    private const string OfficialReadmePrefix = "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/plugins/";

    private readonly Func<AppSettings> _settings;
    private readonly Func<PluginManager> _plugins;
    private readonly PluginPackageService _packages;
    private readonly OutboundHttpClientProvider _outbound;
    private readonly object _readmeSync = new();
    private readonly Dictionary<string, PluginReadmeResult> _localReadmeCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginReadmeCacheEntry> _officialReadmeCache = new(StringComparer.Ordinal);

    private readonly PluginRepositoryCatalogCache _catalogCache;
    private readonly PluginStoreProjector _storeProjector;
    private readonly PluginRepositoryOperations _operations;

    public PluginRepositoryService(
        Func<AppSettings> settings,
        Func<PluginManager> plugins,
        PluginPackageService packages,
        OutboundHttpClientProvider? outbound = null)
    {
        _settings = settings;
        _plugins = plugins;
        _packages = packages;
        _outbound = outbound ?? new OutboundHttpClientProvider(settings);
        _catalogCache = new PluginRepositoryCatalogCache();
        _storeProjector = new PluginStoreProjector(_plugins);
        _operations = new PluginRepositoryOperations(
            _plugins,
            _packages,
            RequireEntryAsync,
            () => _plugins().InvalidateManagementSnapshot());
    }

    public async Task<PluginStoreSnapshot> GetStoreAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        string source = PluginRepositoryCatalogCache.CurrentSource();
        PluginCatalogCacheState cached = _catalogCache.Read();
        if (!PluginRepositoryCatalogCache.NeedsValidation(cached, source, forceRefresh))
        {
            return BuildSnapshot(
                cached.Catalog!,
                stale: cached.Error is not null,
                cached.FetchedAt,
                cached.Error);
        }

        await _catalogCache.WaitForRefreshAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            source = PluginRepositoryCatalogCache.CurrentSource();
            cached = _catalogCache.Read();
            if (!PluginRepositoryCatalogCache.NeedsValidation(cached, source, forceRefresh))
            {
                return BuildSnapshot(
                    cached.Catalog!,
                    stale: cached.Error is not null,
                    cached.FetchedAt,
                    cached.Error);
            }

            try
            {
                PluginCatalogFetchResult fetched = await FetchCatalogAsync(cached, source, cancellationToken).ConfigureAwait(false);
                PluginCatalog catalog;
                DateTimeOffset fetchedAt;
                string contentHash;
                bool writeCatalog;
                if (fetched.NotModified)
                {
                    catalog = cached.Catalog
                        ?? throw new PluginRepositoryException("repository_unavailable", "插件 catalog 条件请求返回 304，但本地没有可用缓存");
                    fetchedAt = cached.FetchedAt;
                    contentHash = cached.ContentHash;
                    writeCatalog = false;
                }
                else
                {
                    catalog = fetched.Catalog
                        ?? throw new PluginRepositoryException("catalog_invalid", "插件 catalog 响应为空");
                    contentHash = fetched.ContentHash;
                    writeCatalog = cached.Catalog is null
                        || !string.Equals(cached.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase);
                    fetchedAt = writeCatalog ? fetched.ContentFetchedAt : cached.FetchedAt;
                }

                string etag = string.IsNullOrWhiteSpace(fetched.ETag)
                    ? cached.ETag
                    : fetched.ETag!;
                DateTimeOffset? lastModified = fetched.LastModified ?? cached.LastModified;
                _catalogCache.SetCatalog(
                    catalog,
                    fetchedAt,
                    fetched.CheckedAt,
                    source,
                    etag,
                    lastModified,
                    contentHash);
                _catalogCache.SavePersistent(
                    catalog,
                    fetchedAt,
                    source,
                    fetched.CheckedAt,
                    etag,
                    lastModified,
                    contentHash,
                    writeCatalog);
                return BuildSnapshot(catalog, stale: false, fetchedAt, error: null);
            }
            catch (Exception ex)
            {
                string message = ex is PluginRepositoryException repository
                    ? repository.Message
                    : $"读取插件仓库失败：{ex.Message}";
                DateTimeOffset failedAt = DateTimeOffset.UtcNow;
                _catalogCache.SetError(message, failedAt);
                cached = _catalogCache.Read();
                if (cached.Catalog is null)
                {
                    return PluginStoreSnapshot.Unavailable(message);
                }
                return BuildSnapshot(cached.Catalog, stale: true, cached.FetchedAt, message);
            }
        }
        finally
        {
            _catalogCache.ReleaseRefresh();
        }
    }

    /// <summary>返回当前 catalog 中实际可登记更新的插件；ownership 只描述来源，不参与更新资格。</summary>
    public async Task<IReadOnlyList<PluginStoreItem>> GetUpdateCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        PluginStoreSnapshot snapshot = await GetStoreAsync(false, cancellationToken).ConfigureAwait(false);
        if (!snapshot.Available)
        {
            throw new PluginRepositoryException(
                "repository_unavailable",
                snapshot.Error ?? "插件仓库暂不可用");
        }
        return snapshot.Plugins.Where(IsUpdateEligible).ToArray();
    }

    /// <summary>
    /// 批量更新与单插件更新共用的资格投影。ManagedByStore 是管理信息，不能阻止
    /// 身份与 catalog 匹配的手动安装或历史遗留插件获得官方更新。
    /// </summary>
    internal static bool IsUpdateEligible(PluginStoreItem plugin) =>
        PluginStoreProjector.IsUpdateEligible(plugin);

    internal static string ResolveStoreStatus(
        PluginCompatibilityResult compatibility,
        bool installed,
        bool updateAvailable,
        bool pending) =>
        PluginStoreProjector.ResolveStoreStatus(compatibility, installed, updateAvailable, pending);

    private async Task<PluginCatalogEntry> RequireEntryAsync(
        string name,
        CancellationToken cancellationToken)
    {
        if (!PluginRepositoryCatalog.IsCanonicalPluginId(name))
        {
            throw new PluginRepositoryException("invalid_name", "插件名称无效");
        }
        PluginStoreSnapshot snapshot = await GetStoreAsync(false, cancellationToken).ConfigureAwait(false);
        if (!snapshot.Available)
        {
            throw new PluginRepositoryException(
                "repository_unavailable",
                snapshot.Error ?? "插件仓库暂不可用");
        }
        PluginCatalogEntry? entry = snapshot.Catalog!.Plugins.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        return entry ?? throw new PluginRepositoryException("not_found", $"插件仓库中不存在：{name}");
    }

    private static bool HasPending(string name)
    {
        return PluginInstallRecovery.ReadPending()
            .Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<PluginCatalogFetchResult> FetchCatalogAsync(
        PluginCatalogCacheState cached,
        string source,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
        {
            throw new PluginRepositoryException("repository_unavailable", "插件 catalog 地址无效");
        }
        var policy = new UpdateSourcePolicy(source);
        using HttpClient client = _outbound.CreateClient(
            uri,
            TimeSpan.FromSeconds(30),
            allowAutoRedirect: false);
        using HttpResponseMessage response = await policy.GetAsync(
            client,
            uri,
            UpdateResourceKind.Manifest,
            "NexusPipeline-plugin-catalog/" + UpdateService.CurrentVersion,
            cancellationToken,
            request => AddConditionalHeaders(
                request,
                string.Equals(cached.SourceUrl, source, StringComparison.OrdinalIgnoreCase)
                    ? cached.ETag
                    : "",
                string.Equals(cached.SourceUrl, source, StringComparison.OrdinalIgnoreCase)
                    ? cached.LastModified
                    : null)).ConfigureAwait(false);
        DateTimeOffset checkedAt = DateTimeOffset.UtcNow;
        string? etag = response.Headers.ETag?.ToString();
        DateTimeOffset? lastModified = ReadLastModified(response);
        if (response.StatusCode == HttpStatusCode.NotModified && cached.Catalog is not null)
        {
            return new PluginCatalogFetchResult(
                null,
                cached.FetchedAt,
                checkedAt,
                etag,
                lastModified,
                cached.ContentHash,
                NotModified: true);
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new PluginRepositoryException("repository_unavailable", $"插件 catalog 请求失败：HTTP {(int)response.StatusCode}");
        }
        if (response.Content.Headers.ContentLength is long length && length > PluginRepositoryCatalog.MaxCatalogBytes)
        {
            throw new PluginRepositoryException("catalog_too_large", "插件 catalog 超过尺寸上限");
        }
        string json = await ReadBoundedTextAsync(
            response.Content,
            PluginRepositoryCatalog.MaxCatalogBytes,
            cancellationToken).ConfigureAwait(false);
        if (!PluginRepositoryCatalog.TryParse(json, out PluginCatalog? catalog, out string? error)
            || catalog is null)
        {
            throw new PluginRepositoryException("catalog_invalid", error ?? "插件 catalog 无效");
        }
        return new PluginCatalogFetchResult(
            catalog,
            checkedAt,
            checkedAt,
            etag,
            lastModified,
            PluginRepositoryCatalogCache.ComputeHash(catalog),
            NotModified: false);
    }

    private PluginStoreSnapshot BuildSnapshot(
        PluginCatalog catalog,
        bool stale,
        DateTimeOffset fetchedAt,
        string? error) =>
        _storeProjector.Project(catalog, stale, fetchedAt, error);
}

internal sealed record PluginStoreSnapshot(
    bool Available,
    bool Stale,
    DateTimeOffset FetchedAt,
    string? Error,
    PluginCatalog? Catalog,
    IReadOnlyList<PluginStoreItem> Plugins)
{
    public static PluginStoreSnapshot Unavailable(string error)
    {
        return new PluginStoreSnapshot(false, false, DateTimeOffset.MinValue, error, null, Array.Empty<PluginStoreItem>());
    }
}

internal sealed record PluginStoreItem(
    string Name,
    string ArtifactName,
    string DisplayName,
    string GameName,
    string Description,
    string Version,
    string Kind,
    string ApiVersion,
    IReadOnlyList<string> Capabilities,
    string MinHostVersion,
    bool Installed,
    string InstalledVersion,
    bool UpdateAvailable,
    bool Compatible,
    string CompatibilityReason,
    bool ManagedByStore,
    string PendingAction,
    string PendingVersion,
    string Status,
    string InstalledName,
    IReadOnlyList<PluginChangelogEntry> Changelog)
{
    public IReadOnlyList<PluginAuthor> Authors { get; init; } = Array.Empty<PluginAuthor>();

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public string Homepage { get; init; } = "";

    public string CreatedAt { get; init; } = "";

    public string UpdatedAt { get; init; } = "";

    public bool HasReadme { get; init; }

    public string? CompatibilityCode { get; init; }

    public IReadOnlyDictionary<string, PluginLocalizedMetadata> Locales { get; init; } =
        new Dictionary<string, PluginLocalizedMetadata>(StringComparer.OrdinalIgnoreCase);
}
