using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

/// <summary>插件 catalog 的持久化缓存、条件请求元数据和有界读取工具。</summary>
internal sealed partial class PluginRepositoryService
{
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
        return Encoding.UTF8.GetString(buffer.ToArray());
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
