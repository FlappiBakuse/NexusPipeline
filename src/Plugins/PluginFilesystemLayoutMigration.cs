using NexusPipeline.Plugins.Managed;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;
using System.Text.Json.Nodes;

namespace NexusPipeline.Plugins;

/// <summary>将 schema 1 插件的机器 ID 目录迁移到 schema 2 的正式 artifactName 目录。</summary>
internal static class PluginFilesystemLayoutMigration
{
    private static readonly object Sync = new();
    private static readonly HashSet<string> ConflictedNames = new(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> LegacyArtifactNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bettergi"] = "BetterGI",
            ["maaend"] = "MaaEnd",
            ["march7th"] = "March7thAssistant",
            ["zzzonedragon"] = "ZenlessZoneZeroOneDragon",
            ["game-checkin"] = "GameCheckIn",
            ["custom-wallpaper"] = "CustomWallpaper",
            ["hoyolab-checkin"] = "HoYoLABCheckIn",
        };

    /// <summary>返回已知旧插件的正式物理目录名；未知插件返回 null。</summary>
    public static string? GetCanonicalArtifactName(string pluginName)
    {
        if (LegacyArtifactNames.TryGetValue(pluginName, out string? artifactName))
        {
            return artifactName;
        }
        return TryReadCachedArtifactName(pluginName);
    }

    public static bool HasConflict(string pluginName)
    {
        lock (Sync)
        {
            return ConflictedNames.Contains(pluginName);
        }
    }

    /// <summary>
    /// 启动时迁移插件目录。检测到同一 artifact 的多个物理目录时保留现场并返回 false，
    /// 调用方应阻止后续自动安装/更新。
    /// </summary>
    public static bool Migrate(string? pluginsDir = null)
    {
        string root = Path.GetFullPath(pluginsDir ?? AppPaths.PluginsDir);
        lock (Sync)
        {
            ConflictedNames.Clear();
        }
        if (!Directory.Exists(root))
        {
            return true;
        }

        IReadOnlyDictionary<string, string> cachedArtifactNames = ReadCachedArtifactNames();
        bool success = true;
        string[] directories = Directory.GetDirectories(root);
        foreach (string directory in directories)
        {
            string directoryName = Path.GetFileName(directory);
            if (directoryName.StartsWith(".nxp-rename-", StringComparison.Ordinal))
            {
                continue;
            }
            if (!PluginManifest.TryLoad(directory, out PluginManifest? manifest, out string? error)
                || manifest is null)
            {
                if (File.Exists(Path.Combine(directory, "plugin.json")))
                {
                    Logger.Warn($"[插件] 无法读取目录布局迁移清单：{directoryName}（{error}）");
                }
                continue;
            }

            string? targetName = manifest.SchemaVersion >= 2
                ? manifest.ArtifactName
                : cachedArtifactNames.TryGetValue(manifest.Name, out string? cachedArtifact)
                    ? cachedArtifact
                    : GetCanonicalArtifactName(manifest.Name);
            if (string.IsNullOrWhiteSpace(targetName)
                || string.Equals(directoryName, targetName, StringComparison.Ordinal))
            {
                continue;
            }

            string[] equivalentDirectories = Directory.GetDirectories(root)
                .Where(item => string.Equals(Path.GetFileName(item), targetName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (equivalentDirectories.Length > 1
                || equivalentDirectories.Any(item => !string.Equals(item, directory, StringComparison.OrdinalIgnoreCase)))
            {
                success = false;
                lock (Sync)
                {
                    ConflictedNames.Add(manifest.Name);
                }
                Logger.Error($"[插件] 检测到 artifactName 布局冲突，保留所有目录并暂停自动处理：{manifest.Name} -> {targetName}");
                continue;
            }

            string targetPath = Path.Combine(root, targetName);
            try
            {
                RenameWithTemporaryHop(directory, targetPath, root);
                Logger.Info($"[插件] 已迁移物理目录：{directoryName} -> {targetName}（机器 ID 保持 {manifest.Name}）");
            }
            catch (Exception ex)
            {
                success = false;
                lock (Sync)
                {
                    ConflictedNames.Add(manifest.Name);
                }
                Logger.Error($"[插件] 迁移物理目录失败：{directoryName} -> {targetName}：{ex.Message}");
            }
        }
        return success;
    }

    private static IReadOnlyDictionary<string, string> ReadCachedArtifactNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(AppPaths.PluginCatalogCachePath))
            {
                return result;
            }
            JsonNode? node = JsonNode.Parse(File.ReadAllText(AppPaths.PluginCatalogCachePath));
            if (node is not JsonObject root
                || root["catalog"] is not JsonNode catalogNode
                || !PluginRepositoryCatalog.TryParse(catalogNode.ToJsonString(), out PluginCatalog? catalog, out _)
                || catalog is null
                || catalog.SchemaVersion != PluginRepositoryCatalog.SchemaVersion)
            {
                return result;
            }
            foreach (PluginCatalogEntry entry in catalog.Plugins)
            {
                if (PluginRepositoryCatalog.IsCanonicalPluginId(entry.Name)
                    && PluginRepositoryCatalog.IsSafeArtifactName(entry.ArtifactName))
                {
                    result[entry.Name] = entry.ArtifactName;
                }
            }
        }
        catch
        {
            // 缓存仅用于辅助旧目录推断；读取失败时继续使用内置映射或保留旧目录。
        }
        return result;
    }

    private static string? TryReadCachedArtifactName(string pluginName)
    {
        return ReadCachedArtifactNames().TryGetValue(pluginName, out string? artifactName)
            ? artifactName
            : null;
    }

    private static void RenameWithTemporaryHop(string sourcePath, string targetPath, string root)
    {
        string temporaryPath = Path.Combine(root, ".nxp-rename-" + Guid.NewGuid().ToString("N"));
        Directory.Move(sourcePath, temporaryPath);
        try
        {
            Directory.Move(temporaryPath, targetPath);
        }
        catch
        {
            try
            {
                if (Directory.Exists(temporaryPath) && !Directory.Exists(sourcePath))
                {
                    Directory.Move(temporaryPath, sourcePath);
                }
            }
            catch (Exception rollbackEx)
            {
                Logger.Error($"[插件] 物理目录迁移回滚失败，现场保留在 {temporaryPath}：{rollbackEx.Message}");
            }
            throw;
        }
    }
}
