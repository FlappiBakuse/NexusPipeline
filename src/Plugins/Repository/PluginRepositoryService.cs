using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services.Networking;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

/// <summary>官方插件 catalog 缓存、商店状态合并和生命周期操作编排。</summary>
internal sealed class PluginRepositoryService
{
    private readonly Func<PluginManager> _plugins;
    private readonly PluginPackageService _packages;
    private readonly OutboundHttpClientProvider _outbound;
    private readonly PluginReadmeService _readmes;

    private readonly PluginRepositoryCatalogCache _catalogCache;
    private readonly PluginStoreProjector _storeProjector;
    private readonly PluginRepositoryOperations _operations;

    public PluginRepositoryService(
        Func<AppSettings> settings,
        Func<PluginManager> plugins,
        PluginPackageService packages,
        OutboundHttpClientProvider? outbound = null)
    {
        _plugins = plugins;
        _packages = packages;
        _outbound = outbound ?? new OutboundHttpClientProvider(settings);
        _readmes = new PluginReadmeService(uri => _outbound.CreateClient(
            uri,
            TimeSpan.FromSeconds(30),
            allowAutoRedirect: false));
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
                PluginCatalogFetchResult fetched = await _catalogCache.FetchAsync(
                    cached,
                    source,
                    _outbound,
                    cancellationToken).ConfigureAwait(false);
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

    public Task<PluginPendingOperation> InstallAsync(
        string name,
        bool update,
        CancellationToken cancellationToken = default) =>
        _operations.InstallAsync(name, update, cancellationToken);

    public Task<PluginPendingOperation> UninstallAsync(
        string name,
        CancellationToken cancellationToken = default) =>
        _operations.UninstallAsync(name, cancellationToken);

    public async Task<PluginDetail?> GetLocalDetailAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PluginRepositoryCatalog.IsCanonicalPluginId(name))
        {
            return null;
        }
        PluginManager manager = _plugins();
        PluginManagementView? view = manager.PluginManagementViews.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (view is null || !manager.TryGetPluginDirectory(view.Name, out string? directory) || directory is null)
        {
            return null;
        }
        PluginReadmeResult readme = await _readmes.LoadLocalAsync(directory, cancellationToken).ConfigureAwait(false);
        return new PluginDetail(
            view.Name,
            view.ArtifactName,
            view.DisplayName,
            view.GameName,
            view.Description,
            view.Version,
            view.Kind,
            view.ApiVersion,
            view.Capabilities,
            view.MinHostVersion,
            !string.Equals(view.State, PluginRuntimeState.Incompatible.ToString(), StringComparison.Ordinal),
            view.InstalledName,
            view.InstalledVersion,
            false,
            view.RuntimeErrorCode != "plugin_incompatible_host",
            "",
            view.ManagedByStore,
            view.PendingAction,
            view.PendingVersion,
            view.State.ToLowerInvariant(),
            view.ConfiguredEnabled,
            view.RuntimeEnabled,
            view.State,
            view.Error,
            view.RestartRequired,
            view.HasFrontend,
            view.FrontendApiVersion,
            view.Authors,
            view.Tags,
            view.Homepage,
            view.CreatedAt,
            view.UpdatedAt,
            readme.HasReadme,
            readme.Markdown,
            readme.Error,
            view.Changelog,
            view.Locales)
        {
            RuntimeErrorCode = view.RuntimeErrorCode,
            CompatibilityCode = view.RuntimeErrorCode switch
            {
                "plugin_incompatible_host" => "host_version_too_low",
                "plugin_incompatible_api" => "plugin_api_incompatible",
                _ when string.Equals(view.State, PluginRuntimeState.Incompatible.ToString(), StringComparison.Ordinal)
                    => "incompatible",
                _ => null,
            },
        };
    }

    public async Task<PluginDetail?> GetStoreDetailAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PluginRepositoryCatalog.IsCanonicalPluginId(name))
        {
            return null;
        }
        PluginStoreSnapshot snapshot = await GetStoreAsync(false, cancellationToken).ConfigureAwait(false);
        if (!snapshot.Available)
        {
            throw new PluginRepositoryException(
                "repository_unavailable",
                snapshot.Error ?? "插件仓库暂不可用");
        }
        PluginStoreItem? item = snapshot.Plugins.FirstOrDefault(plugin =>
            string.Equals(plugin.Name, name, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return null;
        }
        PluginManagementView? installedView = _plugins().PluginManagementViews.FirstOrDefault(view =>
            string.Equals(view.Name, item.InstalledName, StringComparison.OrdinalIgnoreCase));
        PluginReadmeResult readme = await _readmes.LoadOfficialAsync(item, cancellationToken).ConfigureAwait(false);
        return new PluginDetail(
            item.Name,
            item.ArtifactName,
            item.DisplayName,
            item.GameName,
            item.Description,
            item.Version,
            item.Kind,
            item.ApiVersion,
            item.Capabilities,
            item.MinHostVersion,
            item.Installed,
            item.InstalledName,
            item.InstalledVersion,
            item.UpdateAvailable,
            item.Compatible,
            item.CompatibilityReason,
            item.ManagedByStore,
            item.PendingAction,
            item.PendingVersion,
            item.Status,
            installedView?.ConfiguredEnabled ?? false,
            installedView?.RuntimeEnabled ?? false,
            installedView?.State ?? "",
            installedView?.Error,
            installedView?.RestartRequired ?? false,
            installedView?.HasFrontend ?? false,
            installedView?.FrontendApiVersion ?? "",
            item.Authors,
            item.Tags,
            item.Homepage,
            item.CreatedAt,
            item.UpdatedAt,
            readme.HasReadme,
            readme.Markdown,
            readme.Error,
            item.Changelog,
            item.Locales)
        {
            RuntimeErrorCode = installedView?.RuntimeErrorCode,
            CompatibilityCode = item.CompatibilityCode,
        };
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
