using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

internal sealed record PluginCatalogCacheState(
    PluginCatalog? Catalog,
    DateTimeOffset FetchedAt,
    DateTimeOffset LastCheckedAt,
    string SourceUrl,
    string ETag,
    DateTimeOffset? LastModified,
    string ContentHash,
    string? Error);

internal sealed record PluginCatalogFetchResult(
    PluginCatalog? Catalog,
    DateTimeOffset ContentFetchedAt,
    DateTimeOffset CheckedAt,
    string? ETag,
    DateTimeOffset? LastModified,
    string ContentHash,
    bool NotModified);

/// <summary>拥有插件 catalog 的内存状态和持久化缓存。</summary>
internal sealed class PluginRepositoryCatalogCache
{
    private static readonly TimeSpan MemoryCacheTtl = TimeSpan.FromMinutes(5);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private PluginCatalog? _catalog;
    private DateTimeOffset _fetchedAt;
    private DateTimeOffset _lastCheckedAt;
    private string _catalogSourceUrl = "";
    private string _catalogEtag = "";
    private DateTimeOffset? _catalogLastModified;
    private string _catalogContentHash = "";
    private string? _lastError;

    internal PluginRepositoryCatalogCache()
    {
        TryLoadPersistentCache();
    }

    internal PluginCatalogCacheState Read()
    {
        lock (_sync)
        {
            return new PluginCatalogCacheState(
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

    internal Task WaitForRefreshAsync(CancellationToken cancellationToken)
    {
        return _refreshGate.WaitAsync(cancellationToken);
    }

    internal void ReleaseRefresh()
    {
        _refreshGate.Release();
    }

    internal void SetCatalog(
        PluginCatalog catalog,
        DateTimeOffset fetchedAt,
        DateTimeOffset lastCheckedAt,
        string sourceUrl,
        string etag,
        DateTimeOffset? lastModified,
        string contentHash)
    {
        lock (_sync)
        {
            _catalog = catalog;
            _fetchedAt = fetchedAt;
            _lastCheckedAt = lastCheckedAt;
            _catalogSourceUrl = sourceUrl;
            _catalogEtag = etag;
            _catalogLastModified = lastModified;
            _catalogContentHash = contentHash;
            _lastError = null;
        }
    }

    internal void SetError(string message, DateTimeOffset checkedAt)
    {
        lock (_sync)
        {
            _lastError = message;
            _lastCheckedAt = checkedAt;
        }
    }

    internal void SavePersistent(
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

    internal static bool NeedsValidation(
        PluginCatalogCacheState cached,
        string source,
        bool forceRefresh)
    {
        return forceRefresh
            || cached.Catalog is null
            || !string.Equals(cached.SourceUrl, source, StringComparison.OrdinalIgnoreCase)
            || cached.LastCheckedAt == DateTimeOffset.MinValue
            || DateTimeOffset.UtcNow - cached.LastCheckedAt >= MemoryCacheTtl;
    }

    internal static string CurrentSource()
    {
        return TestHooks.PluginCatalogUrl ?? PluginRepositoryCatalog.CatalogUrl;
    }

    internal static string ComputeHash(PluginCatalog catalog)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(catalog, JsonOpts.Web);
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
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

            string source = CurrentSource();
            string sourceUrl = "";
            string etag = "";
            DateTimeOffset? lastModified = null;
            DateTimeOffset lastCheckedAt = DateTimeOffset.MinValue;
            string contentHash = ComputeHash(catalog);
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
            lock (_sync)
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
}

internal sealed class PluginCatalogCacheMetadata
{
    public int SchemaVersion { get; set; }

    public string SourceUrl { get; set; } = "";

    public string ETag { get; set; } = "";

    public DateTimeOffset? LastModified { get; set; }

    public DateTimeOffset LastCheckedAt { get; set; }

    public string ContentHash { get; set; } = "";
}
