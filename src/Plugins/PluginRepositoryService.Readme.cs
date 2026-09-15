using System.Net;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services.Networking;
using NexusPipeline.Services.Update;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

/// <summary>插件详情投影与本地/官方 README 缓存。</summary>
internal sealed partial class PluginRepositoryService
{
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
                UpdateResourceKind.ReleaseAsset,
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
}
