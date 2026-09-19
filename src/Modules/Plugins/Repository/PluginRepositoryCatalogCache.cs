using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Testing;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Common;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.Modules.Plugins.Repository;

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
    private readonly PluginRepositorySourceContext _sourceContext;
    private readonly HostVersionInfo _hostVersion;
    private readonly string _catalogCachePath;
    private readonly string _catalogMetaPath;
    private PluginCatalog? _catalog;
    private DateTimeOffset _fetchedAt;
    private DateTimeOffset _lastCheckedAt;
    private string _catalogSourceUrl = "";
    private string _catalogEtag = "";
    private DateTimeOffset? _catalogLastModified;
    private string _catalogContentHash = "";
    private string? _lastError;

    internal PluginRepositoryCatalogCache()
        : this(PluginRepositorySourceContext.Stable, HostVersionInfo.Current)
    {
    }

    internal PluginRepositoryCatalogCache(
        PluginRepositorySourceContext sourceContext,
        HostVersionInfo? hostVersion = null)
    {
        _sourceContext = sourceContext;
        _hostVersion = hostVersion ?? HostVersionInfo.Current;
        string cacheRoot = sourceContext.IsDevelop
            ? Path.Combine(AppPaths.PluginStateDir, "develop")
            : AppPaths.PluginStateDir;
        _catalogCachePath = Path.Combine(cacheRoot, "catalog-cache.json");
        _catalogMetaPath = Path.Combine(cacheRoot, "catalog-cache.meta.json");
        TryLoadPersistentCache();
    }

    internal string CurrentSource => TestHooks.PluginCatalogUrl ?? _sourceContext.CatalogUri;

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

    internal async Task<PluginCatalogFetchResult> FetchAsync(
        PluginCatalogCacheState cached,
        string source,
        OutboundHttpClientProvider outbound,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
        {
            throw new PluginRepositoryException("repository_unavailable", "插件 catalog 地址无效");
        }

        var policy = new RemoteResourcePolicy(PluginRemoteResourceRules.ForCatalog(uri));
        using HttpClient client = outbound.CreateClient(
            uri,
            TimeSpan.FromSeconds(30),
            allowAutoRedirect: false);
        using HttpResponseMessage response = await policy.GetAsync(
            client,
            uri,
            "NexusPipeline-plugin-catalog/" + _hostVersion.CurrentVersion,
            cancellationToken,
            request => AddConditionalHeaders(
                request,
                IsSameSource(cached, source)
                    ? cached.ETag
                    : "",
                IsSameSource(cached, source)
                    ? cached.LastModified
                    : null)).ConfigureAwait(false);
        DateTimeOffset checkedAt = DateTimeOffset.UtcNow;
        string? etag = response.Headers.ETag?.ToString();
        DateTimeOffset? lastModified = ReadLastModified(response);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            if (!IsSameSource(cached, source) || cached.Catalog is null)
            {
                throw new PluginRepositoryException(
                    "catalog_invalid",
                    "插件 catalog 返回 304，但当前来源没有可用的同源缓存");
            }
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
        if (!PluginRepositoryCatalog.TryParse(json, _sourceContext, out PluginCatalog? catalog, out string? error)
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
            ComputeHash(catalog),
            NotModified: false);
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
            Directory.CreateDirectory(Path.GetDirectoryName(_catalogCachePath)!);
            if (writeCatalog || !File.Exists(_catalogCachePath))
            {
                JsonNode? catalogNode = JsonNode.Parse(JsonSerializer.Serialize(catalog, JsonOpts.Web));
                var root = new JsonObject
                {
                    ["SchemaVersion"] = 1,
                    ["FetchedAt"] = fetchedAt.ToString("O"),
                    ["Catalog"] = catalogNode,
                };
                JsonUtil.WriteAtomic(_catalogCachePath, root.ToJsonString(JsonOpts.Indented));
            }

            var metadata = new PluginCatalogCacheMetadata
            {
                SchemaVersion = 1,
                Channel = _sourceContext.Channel,
                SourceUrl = sourceUrl,
                ETag = etag,
                LastModified = lastModified,
                LastCheckedAt = lastCheckedAt,
                ContentHash = contentHash,
            };
            JsonUtil.WriteAtomic(
                _catalogMetaPath,
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
            || !string.Equals(cached.SourceUrl, source, StringComparison.Ordinal)
            || cached.LastCheckedAt == DateTimeOffset.MinValue
            || DateTimeOffset.UtcNow - cached.LastCheckedAt >= MemoryCacheTtl;
    }

    internal static string ComputeHash(PluginCatalog catalog)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(catalog, JsonOpts.Web);
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
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
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > maxBytes)
            {
                throw new PluginRepositoryException("catalog_too_large", "插件 catalog 超过尺寸上限");
            }
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private void TryLoadPersistentCache()
    {
        try
        {
            if (!File.Exists(_catalogCachePath))
            {
                return;
            }
            JsonNode? node = JsonNode.Parse(File.ReadAllText(_catalogCachePath));
            if (node is not JsonObject root
                || GetProperty(root, "catalog") is not JsonNode catalogNode
                || !DateTimeOffset.TryParse(GetProperty(root, "fetchedAt")?.ToString(), out DateTimeOffset fetchedAt)
                || !PluginRepositoryCatalog.TryParse(catalogNode.ToJsonString(), _sourceContext, out PluginCatalog? catalog, out _)
                || catalog is null)
            {
                return;
            }

            string source = CurrentSource;
            string sourceUrl = "";
            string etag = "";
            DateTimeOffset? lastModified = null;
            DateTimeOffset lastCheckedAt = DateTimeOffset.MinValue;
            string contentHash = ComputeHash(catalog);
            bool metadataMatches = false;
            if (File.Exists(_catalogMetaPath))
            {
                try
                {
                    PluginCatalogCacheMetadata? metadata = JsonSerializer.Deserialize<PluginCatalogCacheMetadata>(
                        File.ReadAllText(_catalogMetaPath),
                        JsonOpts.Default);
                    if (metadata is not null
                        && metadata.SchemaVersion == 1
                        && string.Equals(metadata.Channel, _sourceContext.Channel, StringComparison.Ordinal)
                        && string.Equals(metadata.SourceUrl, source, StringComparison.Ordinal)
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
                        metadataMatches = string.IsNullOrWhiteSpace(metadata.ContentHash)
                            || string.Equals(metadata.ContentHash, ComputeHash(catalog), StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[插件] 读取 catalog 缓存元数据失败，将重新验证：{ex.Message}");
                }
            }
            if (!metadataMatches)
            {
                return;
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

    private static bool IsSameSource(PluginCatalogCacheState cached, string source)
    {
        return cached.Catalog is not null
            && cached.LastCheckedAt > DateTimeOffset.MinValue
            && string.Equals(cached.SourceUrl, source, StringComparison.Ordinal);
    }
}

internal sealed class PluginCatalogCacheMetadata
{
    public int SchemaVersion { get; set; }

    public string Channel { get; set; } = "stable";

    public string SourceUrl { get; set; } = "";

    public string ETag { get; set; } = "";

    public DateTimeOffset? LastModified { get; set; }

    public DateTimeOffset LastCheckedAt { get; set; }

    public string ContentHash { get; set; } = "";
}
