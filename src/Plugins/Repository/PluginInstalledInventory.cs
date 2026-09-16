using NexusPipeline.Persistence;
using NexusPipeline.Plugins.Managed;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

/// <summary>读取已安装插件的 manifest 投影，不触发插件程序集或生命周期。</summary>
internal static class PluginInstalledInventory
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, IReadOnlyList<PluginSummary>> Cache = new(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<PluginSummary> ReadSummaries(string? pluginRoot = null)
    {
        string root = Path.GetFullPath(pluginRoot ?? AppPaths.PluginsDir);
        lock (Sync)
        {
            if (Cache.TryGetValue(root, out IReadOnlyList<PluginSummary>? cached))
            {
                return cached;
            }
        }
        if (!Directory.Exists(root))
        {
            return Array.Empty<PluginSummary>();
        }

        var discovered = new List<(string Directory, PluginManifest Manifest)>();
        foreach (string directory in Directory.GetDirectories(root).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            if (!PluginManifest.TryLoad(directory, out PluginManifest? manifest, out string? error)
                || manifest is null)
            {
                Logger.Warn($"[插件] 更新库存忽略无效插件目录：{Path.GetFileName(directory)}（{error}）");
                continue;
            }

            if (!string.Equals(Path.GetFileName(directory), manifest.ArtifactName, StringComparison.Ordinal))
            {
                Logger.Warn($"[插件] 更新库存忽略目录名称不匹配：{Path.GetFileName(directory)}（期望 {manifest.ArtifactName}）");
                continue;
            }

            discovered.Add((directory, manifest));
        }

        var summaries = new List<PluginSummary>();
        foreach (IGrouping<string, (string Directory, PluginManifest Manifest)> group in discovered
            .GroupBy(item => item.Manifest.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() != 1)
            {
                Logger.Warn($"[插件] 更新库存拒绝重复插件名候选组：{group.Key}");
                continue;
            }

            (string directory, PluginManifest manifest) = group.Single();
            PluginPresentationMetadata metadata = PluginPresentationMetadataParser.LoadLocal(
                directory,
                manifest.GameName,
                manifest.Version);
            summaries.Add(new PluginSummary(
                manifest.Name,
                manifest.ArtifactName,
                manifest.DisplayName,
                string.IsNullOrWhiteSpace(metadata.GameName) ? manifest.GameName : metadata.GameName,
                manifest.Description,
                manifest.Version,
                manifest.Kind,
                manifest.ApiVersion,
                manifest.Capabilities.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray(),
                manifest.Frontend is not null,
                manifest.Frontend?.ApiVersion ?? "")
            {
                Authors = metadata.Authors,
                Tags = metadata.Tags,
                Homepage = metadata.Homepage,
                CreatedAt = metadata.CreatedAt,
                UpdatedAt = metadata.UpdatedAt,
                Changelog = metadata.Changelog,
                Locales = metadata.Locales,
                HasReadme = metadata.HasReadme,
                MinHostVersion = manifest.MinHostVersion,
            });
        }

        IReadOnlyList<PluginSummary> result = summaries;
        lock (Sync)
        {
            Cache[root] = result;
        }
        return result;
    }

    internal static void Invalidate(string? pluginRoot = null)
    {
        lock (Sync)
        {
            if (pluginRoot is null)
            {
                Cache.Clear();
            }
            else
            {
                Cache.Remove(Path.GetFullPath(pluginRoot));
            }
        }
    }
}
