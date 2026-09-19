using System.Net;
using System.Text;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Platform.Networking;

namespace NexusPipeline.Modules.Plugins.Repository;

/// <summary>读取本地和官方插件 README，并拥有 README 缓存与受限 UTF-8 解码。</summary>
internal sealed class PluginReadmeService
{
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(5);
    private const long MaxReadmeBytes = 256L * 1024;

    private readonly Func<Uri, HttpClient> _createClient;
    private readonly TimeSpan _cacheTtl;
    private readonly object _sync = new();
    private readonly Dictionary<string, PluginReadmeResult> _localCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginReadmeCacheEntry> _officialCache = new(StringComparer.Ordinal);

    internal PluginReadmeService(
        Func<Uri, HttpClient> createClient,
        TimeSpan? cacheTtl = null)
    {
        _createClient = createClient;
        _cacheTtl = cacheTtl ?? DefaultCacheTtl;
    }

    internal async Task<PluginReadmeResult> LoadLocalAsync(
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
        lock (_sync)
        {
            if (_localCache.TryGetValue(key, out PluginReadmeResult? cached))
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
                string markdown = await ReadBoundedUtf8TextAsync(stream, cancellationToken).ConfigureAwait(false);
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

        lock (_sync)
        {
            _localCache[key] = result;
        }
        return result;
    }

    internal async Task<PluginReadmeResult> LoadOfficialAsync(
        PluginStoreItem item,
        CancellationToken cancellationToken)
    {
        if (!item.HasReadme)
        {
            return new PluginReadmeResult(false, "", null);
        }
        if (!OfficialPluginSourcePaths.TryGetReadmeUri(
                item.Kind,
                item.ArtifactName,
                out Uri? uri,
                out string? sourceError)
            || uri is null)
        {
            return new PluginReadmeResult(true, "", sourceError ?? "官方插件 README 地址无效");
        }

        string key = $"store:{item.Kind}:{item.ArtifactName}:{item.Version}";
        PluginReadmeCacheEntry? cachedEntry;
        lock (_sync)
        {
            _officialCache.TryGetValue(key, out cachedEntry);
            if (cachedEntry is not null
                && DateTimeOffset.UtcNow - cachedEntry.LastCheckedAt < _cacheTtl)
            {
                return cachedEntry.Result;
            }
        }

        PluginReadmeResult result;
        string? responseEtag = null;
        DateTimeOffset? responseLastModified = null;
        try
        {
            using HttpClient client = _createClient(uri);
            using HttpResponseMessage response = await new RemoteResourcePolicy("").GetAsync(
                client,
                uri,
                RemoteResourceKind.ReleaseAssetResource,
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
                lock (_sync)
                {
                    _officialCache[key] = refreshed;
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
            lock (_sync)
            {
                _officialCache[key] = new PluginReadmeCacheEntry(
                    result,
                    DateTimeOffset.UtcNow,
                    responseEtag,
                    responseLastModified);
            }
        }
        return result;
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

    private static async Task<string> ReadBoundedUtf8TextAsync(
        Stream input,
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
            if (buffer.Length + read > MaxReadmeBytes)
            {
                throw new InvalidDataException("README.md 超过 256 KiB 大小上限");
            }
            buffer.Write(chunk, 0, read);
        }
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(buffer.ToArray());
    }
}
