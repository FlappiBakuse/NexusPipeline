using System.Net;
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

    private readonly Func<AppSettings> _settings;
    private readonly Func<PluginManager> _plugins;
    private readonly PluginPackageService _packages;
    private readonly OutboundHttpClientProvider _outbound;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _cacheSync = new();

    private PluginCatalog? _catalog;
    private DateTimeOffset _fetchedAt;
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
        PluginCatalog? catalog;
        DateTimeOffset fetchedAt;
        string? error;
        bool stale;
        lock (_cacheSync)
        {
            catalog = _catalog;
            fetchedAt = _fetchedAt;
            error = _lastError;
            stale = catalog is not null && DateTimeOffset.UtcNow - fetchedAt > MemoryCacheTtl;
        }
        if (!forceRefresh && catalog is not null && !stale)
        {
            return BuildSnapshot(catalog, stale: false, fetchedAt, error: null);
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_cacheSync)
            {
                catalog = _catalog;
                fetchedAt = _fetchedAt;
                error = _lastError;
                stale = catalog is not null && DateTimeOffset.UtcNow - fetchedAt > MemoryCacheTtl;
            }
            if (!forceRefresh && catalog is not null && !stale)
            {
                return BuildSnapshot(catalog, stale: false, fetchedAt, error: null);
            }

            try
            {
                (PluginCatalog fetched, DateTimeOffset timestamp) = await FetchCatalogAsync(cancellationToken).ConfigureAwait(false);
                lock (_cacheSync)
                {
                    _catalog = fetched;
                    _fetchedAt = timestamp;
                    _lastError = null;
                    catalog = fetched;
                    fetchedAt = timestamp;
                    error = null;
                }
                SavePersistentCache(fetched, timestamp);
                return BuildSnapshot(fetched, stale: false, timestamp, error: null);
            }
            catch (Exception ex)
            {
                string message = ex is PluginRepositoryException repository
                    ? repository.Message
                    : $"读取插件仓库失败：{ex.Message}";
                lock (_cacheSync)
                {
                    _lastError = message;
                    catalog = _catalog;
                    fetchedAt = _fetchedAt;
                    error = message;
                }
                if (catalog is null)
                {
                    return PluginStoreSnapshot.Unavailable(message);
                }
                return BuildSnapshot(catalog, stale: true, fetchedAt, error);
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
        if (PluginFilesystemLayoutMigration.HasConflict(entry.Name)
            || entry.ReplacementNames.Any(PluginFilesystemLayoutMigration.HasConflict))
        {
            throw new PluginRepositoryException(
                "layout_conflict",
                $"插件「{entry.Name}」存在物理目录布局冲突，请先处理冲突目录");
        }
        if (!PluginRepositoryCatalog.IsCompatible(entry, UpdateService.CurrentVersion, out string compatibilityReason))
        {
            throw new PluginRepositoryException("incompatible", compatibilityReason);
        }
        PluginSummary? installed = _plugins().PluginSummaries.FirstOrDefault(item =>
            string.Equals(item.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
        PluginSummary? replacementSource = _plugins().PluginSummaries.FirstOrDefault(item =>
            entry.ReplacementNames.Any(name => string.Equals(name, item.Name, StringComparison.OrdinalIgnoreCase)));
        if (installed is not null && replacementSource is not null)
        {
            throw new PluginRepositoryException("replacement_conflict", $"插件替换冲突：{entry.Name} 与 {replacementSource.Name} 同时存在");
        }
        if (update && installed is null)
        {
            if (replacementSource is null)
            {
                throw new PluginRepositoryException("not_installed", $"插件尚未安装：{entry.Name}");
            }
        }
        if (!update && (installed is not null || replacementSource is not null))
        {
            throw new PluginRepositoryException("already_installed", $"插件已安装：{entry.Name}");
        }
        if (HasPending(entry.Name, replacementSource?.Name))
        {
            throw new PluginRepositoryException("pending", $"插件已有待重启事务：{entry.Name}");
        }
        if (update && installed is not null
            && PluginRepositoryCatalog.CompareVersions(installed.Version, entry.Version) >= 0)
        {
            throw new PluginRepositoryException("up_to_date", $"插件已是 v{installed.Version}");
        }
        return await _packages.StageAsync(
            entry,
            update ? "update" : "install",
            replacementSource?.Name,
            replacementSource?.ArtifactName,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<PluginPendingOperation> UninstallAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PluginRepositoryCatalog.IsSafePluginName(name))
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
        return Task.FromResult(operation);
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

    private bool HasPending(string name, string? sourceName = null)
    {
        return PluginInstallRecovery.ReadPending()
            .Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.SourceName, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Name, sourceName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.SourceName, sourceName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<(PluginCatalog Catalog, DateTimeOffset FetchedAt)> FetchCatalogAsync(
        CancellationToken cancellationToken)
    {
        string source = TestHooks.PluginCatalogUrl ?? PluginRepositoryCatalog.CatalogUrl;
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
            cancellationToken).ConfigureAwait(false);
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
        return (catalog, DateTimeOffset.UtcNow);
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
            PluginSummary? replacementSource = installed.Values.FirstOrDefault(item =>
                entry.ReplacementNames.Any(name => string.Equals(name, item.Name, StringComparison.OrdinalIgnoreCase)));
            PluginPendingOperation? operation = pending.LastOrDefault(item =>
                string.Equals(item.Name, entry.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.SourceName, entry.Name, StringComparison.OrdinalIgnoreCase)
                || entry.ReplacementNames.Any(name => string.Equals(name, item.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, item.SourceName, StringComparison.OrdinalIgnoreCase)));
            bool compatible = PluginRepositoryCatalog.IsCompatible(entry, UpdateService.CurrentVersion, out string compatibilityReason);
            bool replacementConflict = local is not null && replacementSource is not null;
            bool layoutConflict = PluginFilesystemLayoutMigration.HasConflict(entry.Name)
                || entry.ReplacementNames.Any(PluginFilesystemLayoutMigration.HasConflict);
            bool updateAvailable = replacementConflict
                || replacementSource is not null
                || local is not null && PluginRepositoryCatalog.CompareVersions(local.Version, entry.Version) < 0;
            PluginSummary? effectiveInstalled = local ?? replacementSource;
            string status = operation is not null
                ? "pending"
                : layoutConflict
                    ? "layout-conflict"
                : replacementConflict
                    ? "replacement-conflict"
                : !compatible
                    ? "incompatible"
                : effectiveInstalled is null
                    ? "not-installed"
                        : replacementSource is not null ? "replacement-available"
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
                effectiveInstalled is not null,
                effectiveInstalled?.Version ?? "",
                updateAvailable,
                compatible,
                compatible ? "" : compatibilityReason,
                ownership.ContainsKey(entry.Name)
                    || replacementSource is not null && ownership.ContainsKey(replacementSource.Name),
                operation?.Action ?? "",
                operation?.Version ?? "",
                status,
                effectiveInstalled?.Name ?? "",
                entry.Changelog));
        }
        foreach (PluginSummary local in installed.Values
                     .Where(plugin => !listedNames.Contains(plugin.Name))
                     .OrderBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase))
        {
            PluginOwnership? localOwnership = ownership.TryGetValue(local.Name, out PluginOwnership? owner)
                ? owner
                : null;
            PluginPendingOperation? operation = pending.LastOrDefault(item =>
                string.Equals(item.Name, local.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.SourceName, local.Name, StringComparison.OrdinalIgnoreCase));
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
                Array.Empty<PluginChangelogEntry>()));
        }
        return new PluginStoreSnapshot(
            true,
            stale,
            fetchedAt,
            error,
            catalog,
            items);
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
                || root["catalog"] is not JsonNode catalogNode
                || !DateTimeOffset.TryParse(root["fetchedAt"]?.ToString(), out DateTimeOffset fetchedAt)
                || !PluginRepositoryCatalog.TryParse(catalogNode.ToJsonString(), out PluginCatalog? catalog, out _)
                || catalog is null)
            {
                return;
            }
            lock (_cacheSync)
            {
                _catalog = catalog;
                _fetchedAt = fetchedAt;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件] 读取 catalog 缓存失败：{ex.Message}");
        }
    }

    private static void SavePersistentCache(PluginCatalog catalog, DateTimeOffset fetchedAt)
    {
        try
        {
            JsonNode? catalogNode = JsonNode.Parse(JsonSerializer.Serialize(catalog, JsonOpts.Web));
            var root = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["fetchedAt"] = fetchedAt.ToString("O"),
                ["catalog"] = catalogNode,
            };
            Directory.CreateDirectory(AppPaths.PluginStateDir);
            JsonUtil.WriteAtomic(AppPaths.PluginCatalogCachePath, root.ToJsonString(JsonOpts.Indented));
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件] 写入 catalog 缓存失败：{ex.Message}");
        }
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
    IReadOnlyList<PluginChangelogEntry> Changelog);
