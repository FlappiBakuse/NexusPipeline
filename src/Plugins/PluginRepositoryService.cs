using System.Net;
using System.Security.Cryptography;
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
internal sealed class PluginRepositoryService
{
    private static readonly TimeSpan MemoryCacheTtl = TimeSpan.FromMinutes(5);
    private const long MaxReadmeBytes = 256L * 1024;
    private const string OfficialReadmePrefix = "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/plugins/";

    private readonly Func<AppSettings> _settings;
    private readonly Func<PluginManager> _plugins;
    private readonly PluginPackageService _packages;
    private readonly OutboundHttpClientProvider _outbound;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _cacheSync = new();
    private readonly object _readmeSync = new();
    private readonly Dictionary<string, PluginReadmeResult> _localReadmeCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginReadmeCacheEntry> _officialReadmeCache = new(StringComparer.Ordinal);

    private PluginCatalog? _catalog;
    private DateTimeOffset _fetchedAt;
    private DateTimeOffset _lastCheckedAt;
    private string _catalogSourceUrl = "";
    private string _catalogEtag = "";
    private DateTimeOffset? _catalogLastModified;
    private string _catalogContentHash = "";
    private string? _lastError;

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
        TryLoadPersistentCache();
    }

    public async Task<PluginStoreSnapshot> GetStoreAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        string source = CurrentCatalogSource();
        CatalogCacheState cached = ReadCatalogCache();
        if (!NeedsCatalogValidation(cached, source, forceRefresh))
        {
            return BuildSnapshot(
                cached.Catalog!,
                stale: cached.Error is not null,
                cached.FetchedAt,
                cached.Error);
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            source = CurrentCatalogSource();
            cached = ReadCatalogCache();
            if (!NeedsCatalogValidation(cached, source, forceRefresh))
            {
                return BuildSnapshot(
                    cached.Catalog!,
                    stale: cached.Error is not null,
                    cached.FetchedAt,
                    cached.Error);
            }

            try
            {
                CatalogFetchResult fetched = await FetchCatalogAsync(cached, source, cancellationToken).ConfigureAwait(false);
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

                lock (_cacheSync)
                {
                    _catalog = catalog;
                    _fetchedAt = fetchedAt;
                    _lastCheckedAt = fetched.CheckedAt;
                    _catalogSourceUrl = source;
                    _catalogEtag = string.IsNullOrWhiteSpace(fetched.ETag)
                        ? cached.ETag
                        : fetched.ETag!;
                    _catalogLastModified = fetched.LastModified ?? cached.LastModified;
                    _catalogContentHash = contentHash;
                    _lastError = null;
                }
                SavePersistentCache(
                    catalog,
                    fetchedAt,
                    source,
                    fetched.CheckedAt,
                    _catalogEtag,
                    _catalogLastModified,
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
                lock (_cacheSync)
                {
                    _lastError = message;
                    _lastCheckedAt = failedAt;
                    cached = new CatalogCacheState(
                        _catalog,
                        _fetchedAt,
                        _lastCheckedAt,
                        _catalogSourceUrl,
                        _catalogEtag,
                        _catalogLastModified,
                        _catalogContentHash,
                        message);
                }
                if (cached.Catalog is null)
                {
                    return PluginStoreSnapshot.Unavailable(message);
                }
                return BuildSnapshot(cached.Catalog, stale: true, cached.FetchedAt, message);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<PluginPendingOperation> InstallAsync(
        string name,
        bool update,
        CancellationToken cancellationToken = default)
    {
        PluginCatalogEntry entry = await RequireEntryAsync(name, cancellationToken).ConfigureAwait(false);
        if (!PluginRepositoryCatalog.IsCompatible(entry, UpdateService.CurrentVersion, out string compatibilityReason))
        {
            throw new PluginRepositoryException("incompatible", compatibilityReason);
        }
        PluginSummary? installed = _plugins().PluginSummaries.FirstOrDefault(item =>
            string.Equals(item.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
        if (update && installed is null)
        {
            throw new PluginRepositoryException("not_installed", $"插件尚未安装：{entry.Name}");
        }
        if (!update && installed is not null)
        {
            throw new PluginRepositoryException("already_installed", $"插件已安装：{entry.Name}");
        }
        if (HasPending(entry.Name))
        {
            throw new PluginRepositoryException("pending", $"插件已有待重启事务：{entry.Name}");
        }
        if (update && installed is not null
            && PluginRepositoryCatalog.CompareVersions(installed.Version, entry.Version) >= 0)
        {
            throw new PluginRepositoryException("up_to_date", $"插件已是 v{installed.Version}");
        }
        PluginPendingOperation operation = await _packages.StageAsync(
            entry,
            update ? "update" : "install",
            cancellationToken).ConfigureAwait(false);
        _plugins().InvalidateManagementSnapshot();
        return operation;
    }

    public Task<PluginPendingOperation> UninstallAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PluginRepositoryCatalog.IsCanonicalPluginId(name))
        {
            throw new PluginRepositoryException("invalid_name", "插件名称无效");
        }
        PluginSummary? installed = _plugins().PluginSummaries.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        PluginOwnership? ownership = PluginInstallRecovery.ReadOwnership()
            .FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            .Value;
        if (installed is null)
        {
            if (ownership is null)
            {
                throw new PluginRepositoryException("not_installed", $"插件尚未安装：{name}");
            }
        }
        string actualName = installed?.Name ?? ownership!.Name;
        if (HasPending(actualName))
        {
            throw new PluginRepositoryException("pending", $"插件已有待重启事务：{actualName}");
        }
        var operation = new PluginPendingOperation
        {
            Action = "uninstall",
            Name = actualName,
            ArtifactName = installed?.ArtifactName ?? ownership!.ArtifactName,
            Version = installed?.Version ?? ownership!.Version,
            Kind = installed?.Kind ?? ownership!.Kind,
            ApiVersion = installed?.ApiVersion ?? ownership!.ApiVersion,
            Phase = "pending",
            StagedPath = Path.Combine(AppPaths.PluginStagingDir, $"uninstall.{actualName}.{Guid.NewGuid():N}"),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        PluginInstallRecovery.AddPending(operation);
        _plugins().InvalidateManagementSnapshot();
        return Task.FromResult(operation);
    }

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
        PluginReadmeResult readme = await LoadLocalReadmeAsync(directory, cancellationToken).ConfigureAwait(false);
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
            "0.0.0",
            true,
            view.InstalledName,
            view.InstalledVersion,
            false,
            true,
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
            view.UpdatedAt,
            readme.HasReadme,
            readme.Markdown,
            readme.Error,
            view.Changelog);
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
        PluginReadmeResult readme = await LoadOfficialReadmeAsync(item, cancellationToken).ConfigureAwait(false);
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
            item.UpdatedAt,
            readme.HasReadme,
            readme.Markdown,
            readme.Error,
            item.Changelog);
    }

    private async Task<PluginReadmeResult> LoadLocalReadmeAsync(
        string pluginDirectory,
        CancellationToken cancellationToken)
    {
        string path = Path.GetFullPath(Path.Combine(pluginDirectory, "README.md"));
        if (!File.Exists(path))
        {
            return new PluginReadmeResult(false, "", null);
        }
        FileInfo file = new(path);
        string key = $"local:{path}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
        lock (_readmeSync)
        {
            if (_localReadmeCache.TryGetValue(key, out PluginReadmeResult? cached))
            {
                return cached;
            }
        }
        PluginReadmeResult result;
        try
        {
            if (file.Length > MaxReadmeBytes)
            {
                result = new PluginReadmeResult(true, "", "README.md 超过 256 KiB 大小上限");
            }
            else
            {
                await using FileStream stream = File.OpenRead(path);
                string markdown = await ReadBoundedUtf8TextAsync(stream, MaxReadmeBytes, cancellationToken).ConfigureAwait(false);
                result = new PluginReadmeResult(true, markdown, null);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = new PluginReadmeResult(true, "", $"读取 README.md 失败：{ex.Message}");
        }
        lock (_readmeSync)
        {
            _localReadmeCache[key] = result;
        }
        return result;
    }

    private async Task<PluginReadmeResult> LoadOfficialReadmeAsync(
        PluginStoreItem item,
        CancellationToken cancellationToken)
    {
        if (!item.HasReadme)
        {
            return new PluginReadmeResult(false, "", null);
        }
        if (!PluginRepositoryCatalog.IsSafeArtifactName(item.ArtifactName))
        {
            return new PluginReadmeResult(true, "", "插件 artifactName 无效");
        }
        string key = $"store:{item.ArtifactName}:{item.Version}";
        PluginReadmeCacheEntry? cachedEntry;
        lock (_readmeSync)
        {
            _officialReadmeCache.TryGetValue(key, out cachedEntry);
            if (cachedEntry is not null
                && DateTimeOffset.UtcNow - cachedEntry.LastCheckedAt < MemoryCacheTtl)
            {
                return cachedEntry.Result;
            }
        }

        PluginReadmeResult result;
        string? responseEtag = null;
        DateTimeOffset? responseLastModified = null;
        try
        {
            Uri uri = new(OfficialReadmePrefix + Uri.EscapeDataString(item.ArtifactName) + "/README.md");
            using HttpClient client = _outbound.CreateClient(uri, TimeSpan.FromSeconds(30), allowAutoRedirect: false);
            using HttpResponseMessage response = await new UpdateSourcePolicy("").GetAsync(
                client,
                uri,
                manifest: false,
                "NexusPipeline-plugin-readme/" + item.Name + "/" + item.Version,
                cancellationToken,
                request => AddConditionalHeaders(request, cachedEntry?.ETag, cachedEntry?.LastModified)).ConfigureAwait(false);
            responseEtag = response.Headers.ETag?.ToString();
            responseLastModified = ReadLastModified(response);
            if (response.StatusCode == HttpStatusCode.NotModified && cachedEntry is not null)
            {
                PluginReadmeCacheEntry refreshed = cachedEntry with
                {
                    LastCheckedAt = DateTimeOffset.UtcNow,
                    ETag = responseEtag ?? cachedEntry.ETag,
                    LastModified = responseLastModified ?? cachedEntry.LastModified,
                };
                lock (_readmeSync)
                {
                    _officialReadmeCache[key] = refreshed;
                }
                return refreshed.Result;
            }
            if (!response.IsSuccessStatusCode)
            {
                result = cachedEntry is not null
                    ? cachedEntry.Result with
                {
                    Error = $"读取 README.md 失败：HTTP {(int)response.StatusCode}（显示上次缓存）",
                }
                    : new PluginReadmeResult(true, "", $"读取 README.md 失败：HTTP {(int)response.StatusCode}");
            }
            else if (response.Content.Headers.ContentLength is long length && length > MaxReadmeBytes)
            {
                result = cachedEntry is not null
                    ? cachedEntry.Result with
                {
                    Error = "README.md 超过 256 KiB 大小上限（显示上次缓存）",
                }
                    : new PluginReadmeResult(true, "", "README.md 超过 256 KiB 大小上限");
            }
            else
            {
                string markdown = await ReadBoundedUtf8TextAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    MaxReadmeBytes,
                    cancellationToken).ConfigureAwait(false);
                result = new PluginReadmeResult(true, markdown, null);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = cachedEntry is not null
                ? cachedEntry.Result with
            {
                Error = $"读取 README.md 失败：{ex.Message}（显示上次缓存）",
            }
                : new PluginReadmeResult(true, "", $"读取 README.md 失败：{ex.Message}");
        }
        if (result.Error is null)
        {
            lock (_readmeSync)
            {
                _officialReadmeCache[key] = new PluginReadmeCacheEntry(
                    result,
                    DateTimeOffset.UtcNow,
                    responseEtag,
                    responseLastModified);
            }
        }
        return result;
    }

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

    private async Task<CatalogFetchResult> FetchCatalogAsync(
        CatalogCacheState cached,
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
            manifest: true,
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
            return new CatalogFetchResult(
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
        return new CatalogFetchResult(
            catalog,
            checkedAt,
            checkedAt,
            etag,
            lastModified,
            ComputeCatalogHash(catalog),
            NotModified: false);
    }

    private PluginStoreSnapshot BuildSnapshot(
        PluginCatalog catalog,
        bool stale,
        DateTimeOffset fetchedAt,
        string? error)
    {
        Dictionary<string, PluginSummary> installed = _plugins().PluginSummaries
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, PluginOwnership> ownership = PluginInstallRecovery.ReadOwnership();
        IReadOnlyList<PluginPendingOperation> pending = PluginInstallRecovery.ReadPending();
        var items = new List<PluginStoreItem>();
        var listedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginCatalogEntry entry in catalog.Plugins)
        {
            listedNames.Add(entry.Name);
            installed.TryGetValue(entry.Name, out PluginSummary? local);
            PluginPendingOperation? operation = pending.LastOrDefault(item =>
                string.Equals(item.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
            bool compatible = PluginRepositoryCatalog.IsCompatible(entry, UpdateService.CurrentVersion, out string compatibilityReason);
            bool updateAvailable = local is not null
                && PluginRepositoryCatalog.CompareVersions(local.Version, entry.Version) < 0;
            string status = operation is not null
                ? "pending"
                : !compatible
                    ? "incompatible"
                : local is null
                    ? "not-installed"
                : updateAvailable ? "update-available" : "installed";
            items.Add(new PluginStoreItem(
                entry.Name,
                entry.ArtifactName,
                entry.DisplayName,
                entry.GameName,
                entry.Description,
                entry.Version,
                entry.Kind,
                entry.ApiVersion,
                entry.Capabilities,
                entry.MinHostVersion,
                local is not null,
                local?.Version ?? "",
                updateAvailable,
                compatible,
                compatible ? "" : compatibilityReason,
                ownership.ContainsKey(entry.Name),
                operation?.Action ?? "",
                operation?.Version ?? "",
                status,
                local?.Name ?? "",
                entry.Changelog)
            {
                Authors = entry.Authors,
                Tags = entry.Tags,
                Homepage = entry.Homepage,
                UpdatedAt = entry.UpdatedAt,
                HasReadme = entry.HasReadme,
            });
        }
        foreach (PluginSummary local in installed.Values
                     .Where(plugin => !listedNames.Contains(plugin.Name))
                     .OrderBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase))
        {
            PluginOwnership? localOwnership = ownership.TryGetValue(local.Name, out PluginOwnership? owner)
                ? owner
                : null;
            PluginPendingOperation? operation = pending.LastOrDefault(item =>
                string.Equals(item.Name, local.Name, StringComparison.OrdinalIgnoreCase));
            items.Add(new PluginStoreItem(
                local.Name,
                local.ArtifactName,
                local.DisplayName,
                local.GameName,
                local.Description,
                local.Version,
                local.Kind,
                local.ApiVersion,
                local.Capabilities,
                "0.0.0",
                true,
                local.Version,
                false,
                true,
                "",
                localOwnership is not null,
                operation?.Action ?? "",
                operation?.Version ?? "",
                operation is not null ? "pending" : "unlisted",
                local.Name,
                local.Changelog)
            {
                Authors = local.Authors,
                Tags = local.Tags,
                Homepage = local.Homepage,
                UpdatedAt = local.UpdatedAt,
                HasReadme = local.HasReadme,
            });
        }
        IReadOnlyList<PluginStoreItem> orderedItems = items
            .OrderBy(item => string.Equals(item.Kind, "data-specialized", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new PluginStoreSnapshot(
            true,
            stale,
            fetchedAt,
            error,
            catalog,
            orderedItems);
    }

    private void TryLoadPersistentCache()
    {
        try
        {
            if (!File.Exists(AppPaths.PluginCatalogCachePath))
            {
                return;
            }
            JsonNode? node = JsonNode.Parse(File.ReadAllText(AppPaths.PluginCatalogCachePath));
            if (node is not JsonObject root
                || GetProperty(root, "catalog") is not JsonNode catalogNode
                || !DateTimeOffset.TryParse(GetProperty(root, "fetchedAt")?.ToString(), out DateTimeOffset fetchedAt)
                || !PluginRepositoryCatalog.TryParse(catalogNode.ToJsonString(), out PluginCatalog? catalog, out _)
                || catalog is null)
            {
                return;
            }

            string source = CurrentCatalogSource();
            string sourceUrl = "";
            string etag = "";
            DateTimeOffset? lastModified = null;
            DateTimeOffset lastCheckedAt = DateTimeOffset.MinValue;
            string contentHash = ComputeCatalogHash(catalog);
            if (File.Exists(AppPaths.PluginCatalogCacheMetaPath))
            {
                try
                {
                    PluginCatalogCacheMetadata? metadata = JsonSerializer.Deserialize<PluginCatalogCacheMetadata>(
                        File.ReadAllText(AppPaths.PluginCatalogCacheMetaPath),
                        JsonOpts.Default);
                    if (metadata is not null
                        && metadata.SchemaVersion == 1
                        && string.Equals(metadata.SourceUrl, source, StringComparison.OrdinalIgnoreCase)
                        && metadata.LastCheckedAt > DateTimeOffset.MinValue)
                    {
                        sourceUrl = metadata.SourceUrl;
                        etag = metadata.ETag ?? "";
                        lastModified = metadata.LastModified;
                        lastCheckedAt = metadata.LastCheckedAt;
                        if (!string.IsNullOrWhiteSpace(metadata.ContentHash))
                        {
                            contentHash = metadata.ContentHash;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[插件] 读取 catalog 缓存元数据失败，将重新验证：{ex.Message}");
                }
            }
            lock (_cacheSync)
            {
                _catalog = catalog;
                _fetchedAt = fetchedAt;
                _lastCheckedAt = lastCheckedAt;
                _catalogSourceUrl = sourceUrl;
                _catalogEtag = etag;
                _catalogLastModified = lastModified;
                _catalogContentHash = contentHash;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件] 读取 catalog 缓存失败：{ex.Message}");
        }
    }

    private static void SavePersistentCache(
        PluginCatalog catalog,
        DateTimeOffset fetchedAt,
        string sourceUrl,
        DateTimeOffset lastCheckedAt,
        string etag,
        DateTimeOffset? lastModified,
        string contentHash,
        bool writeCatalog)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.PluginStateDir);
            if (writeCatalog || !File.Exists(AppPaths.PluginCatalogCachePath))
            {
                JsonNode? catalogNode = JsonNode.Parse(JsonSerializer.Serialize(catalog, JsonOpts.Web));
                var root = new JsonObject
                {
                    ["SchemaVersion"] = 1,
                    ["FetchedAt"] = fetchedAt.ToString("O"),
                    ["Catalog"] = catalogNode,
                };
                JsonUtil.WriteAtomic(AppPaths.PluginCatalogCachePath, root.ToJsonString(JsonOpts.Indented));
            }

            var metadata = new PluginCatalogCacheMetadata
            {
                SchemaVersion = 1,
                SourceUrl = sourceUrl,
                ETag = etag,
                LastModified = lastModified,
                LastCheckedAt = lastCheckedAt,
                ContentHash = contentHash,
            };
            JsonUtil.WriteAtomic(
                AppPaths.PluginCatalogCacheMetaPath,
                JsonSerializer.Serialize(metadata, JsonOpts.Indented));
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件] 写入 catalog 缓存失败：{ex.Message}");
        }
    }

    private CatalogCacheState ReadCatalogCache()
    {
        lock (_cacheSync)
        {
            return new CatalogCacheState(
                _catalog,
                _fetchedAt,
                _lastCheckedAt,
                _catalogSourceUrl,
                _catalogEtag,
                _catalogLastModified,
                _catalogContentHash,
                _lastError);
        }
    }

    private static bool NeedsCatalogValidation(
        CatalogCacheState cached,
        string source,
        bool forceRefresh)
    {
        return forceRefresh
            || cached.Catalog is null
            || !string.Equals(cached.SourceUrl, source, StringComparison.OrdinalIgnoreCase)
            || cached.LastCheckedAt == DateTimeOffset.MinValue
            || DateTimeOffset.UtcNow - cached.LastCheckedAt >= MemoryCacheTtl;
    }

    private static string CurrentCatalogSource()
    {
        return TestHooks.PluginCatalogUrl ?? PluginRepositoryCatalog.CatalogUrl;
    }

    private static JsonNode? GetProperty(JsonObject root, string name)
    {
        foreach ((string key, JsonNode? value) in root)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }
        return null;
    }

    private static void AddConditionalHeaders(
        HttpRequestMessage request,
        string? etag,
        DateTimeOffset? lastModified)
    {
        if (!string.IsNullOrWhiteSpace(etag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }
        if (lastModified is not null)
        {
            request.Headers.IfModifiedSince = lastModified;
        }
    }

    private static DateTimeOffset? ReadLastModified(HttpResponseMessage response)
    {
        if (response.Content.Headers.LastModified is DateTimeOffset contentValue)
        {
            return contentValue;
        }
        if (response.Headers.TryGetValues("Last-Modified", out IEnumerable<string>? values))
        {
            string? value = values.FirstOrDefault();
            if (DateTimeOffset.TryParse(value, out DateTimeOffset parsed))
            {
                return parsed;
            }
        }
        return null;
    }

    private static string ComputeCatalogHash(PluginCatalog catalog)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(catalog, JsonOpts.Web);
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static async Task<string> ReadBoundedTextAsync(
        HttpContent content,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int read = await input.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maxBytes)
            {
                throw new PluginRepositoryException("catalog_too_large", "插件 catalog 超过尺寸上限");
            }
            buffer.Write(chunk, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<string> ReadBoundedUtf8TextAsync(
        Stream input,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int read = await input.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidDataException("README.md 超过 256 KiB 大小上限");
            }
            buffer.Write(chunk, 0, read);
        }
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(buffer.ToArray());
    }

    private sealed record CatalogCacheState(
        PluginCatalog? Catalog,
        DateTimeOffset FetchedAt,
        DateTimeOffset LastCheckedAt,
        string SourceUrl,
        string ETag,
        DateTimeOffset? LastModified,
        string ContentHash,
        string? Error);

    private sealed record CatalogFetchResult(
        PluginCatalog? Catalog,
        DateTimeOffset ContentFetchedAt,
        DateTimeOffset CheckedAt,
        string? ETag,
        DateTimeOffset? LastModified,
        string ContentHash,
        bool NotModified);

    private sealed record PluginReadmeCacheEntry(
        PluginReadmeResult Result,
        DateTimeOffset LastCheckedAt,
        string? ETag,
        DateTimeOffset? LastModified);

    private sealed class PluginCatalogCacheMetadata
    {
        public int SchemaVersion { get; set; }

        public string SourceUrl { get; set; } = "";

        public string ETag { get; set; } = "";

        public DateTimeOffset? LastModified { get; set; }

        public DateTimeOffset LastCheckedAt { get; set; }

        public string ContentHash { get; set; } = "";
    }
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

    public string UpdatedAt { get; init; } = "";

    public bool HasReadme { get; init; }
}
