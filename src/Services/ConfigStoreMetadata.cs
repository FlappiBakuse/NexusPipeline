using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>
/// 用户配置快照的归属与定位元数据。
/// 元数据不复制插件 profile；它只用于判断已有 store 是否仍对应当前配置位置。
/// </summary>
internal sealed class ConfigStoreMetadata
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public long Generation { get; set; }

    public string LastCommittedTransactionId { get; set; } = "";

    public string PluginName { get; set; } = "";

    public string PluginVersion { get; set; } = "";

    public string ProfileHash { get; set; } = "";

    public string ConfigLocatorHash { get; set; } = "";

    public string ConfigKind { get; set; } = "missing";

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public static ConfigStoreMetadata For(
        string configPath,
        ConfigSessionRuntimeMetadata? runtime = null)
    {
        PathKind kind = PathKindUtil.KindOf(configPath);
        return new ConfigStoreMetadata
        {
            SchemaVersion = CurrentSchemaVersion,
            PluginName = runtime?.PluginName ?? "",
            PluginVersion = runtime?.PluginVersion ?? "",
            ProfileHash = runtime?.ProfileHash ?? "",
            ConfigLocatorHash = HashLocator(configPath),
            ConfigKind = PathKindUtil.Text(kind),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    public static ConfigStoreMetadata? Load(string scriptId, string userKey)
    {
        string path = ConfigSwapPaths.StoreMetadataPath(scriptId, userKey);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ConfigStoreMetadata>(File.ReadAllText(path), JsonOpts.Default);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[配置快照] 元数据读取失败（{path}）：{ex.Message}");
            return null;
        }
    }

    public static void Save(string scriptId, string userKey, ConfigStoreMetadata metadata)
    {
        SaveAt(ConfigSwapPaths.StoreMetadataPath(scriptId, userKey), metadata);
    }

    /// <summary>
    /// 为 v0.12.x 已存在的用户快照建立一次性归属基线。
    /// 迁移阶段仍可读取旧脚本中的 ConfigPath，因此能识别后续首次运行时的定位变化。
    /// </summary>
    public static void SeedLegacyStoreMetadata(
        string dataRoot,
        string scriptId,
        string configPath,
        string pluginName)
    {
        string scriptRoot = Path.Combine(Path.GetFullPath(dataRoot), scriptId);
        if (!Directory.Exists(scriptRoot))
        {
            return;
        }

        foreach (string userDir in Directory.GetDirectories(scriptRoot))
        {
            string store = Path.Combine(userDir, "store");
            if (!Directory.Exists(store) && !File.Exists(store))
            {
                continue;
            }
            if (Directory.Exists(store) && !Directory.EnumerateFileSystemEntries(store).Any())
            {
                continue;
            }
            string metadataPath = Path.Combine(userDir, "store-meta.json");
            if (File.Exists(metadataPath)
                || File.Exists(Path.Combine(userDir, "store.meta.json")))
            {
                continue;
            }
            try
            {
                var metadata = new ConfigStoreMetadata
                {
                    PluginName = pluginName ?? "",
                    PluginVersion = "",
                    ProfileHash = "legacy-v0.12.9",
                    ConfigLocatorHash = HashLocator(configPath),
                    ConfigKind = PathKindUtil.Text(PathKindUtil.KindOf(configPath)),
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                SaveAt(metadataPath, metadata);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[配置快照] 迁移旧快照元数据失败（{metadataPath}）：{ex.Message}");
            }
        }
    }

    private static void SaveAt(string path, ConfigStoreMetadata metadata)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonUtil.WriteAtomic(path, JsonSerializer.Serialize(metadata, JsonOpts.Indented));
    }

    private static ConfigStoreMetadata? LoadAt(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ConfigStoreMetadata>(File.ReadAllText(path), JsonOpts.Default);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[配置快照] 元数据读取失败：{path}：{ex.Message}");
            return null;
        }
    }

    public static ConfigStoreMetadata FromMark(ConfigSessionMark mark, ConfigStoreMetadata? existing = null)
    {
        ConfigStoreMetadata expected = For(mark.ConfigPath, new ConfigSessionRuntimeMetadata(
            mark.WorkingDirectory,
            mark.LaunchExe,
            mark.ProcessIdentity,
            mark.ProfileHash,
            mark.PluginName,
            mark.PluginVersion,
            string.IsNullOrWhiteSpace(mark.ConfigKind) ? mark.OriginalKind : mark.ConfigKind));
        if (existing is not null)
        {
            expected.Generation = existing.Generation;
            expected.LastCommittedTransactionId = existing.LastCommittedTransactionId;
            expected.SchemaVersion = Math.Max(existing.SchemaVersion, CurrentSchemaVersion);
        }
        return expected;
    }

    public static void SaveFromMark(string scriptId, string userKey, ConfigSessionMark mark)
    {
        string path = ConfigSwapPaths.StoreMetadataPath(scriptId, userKey);
        ConfigStoreMetadata? existing = Load(scriptId, userKey);
        if (existing is null && File.Exists(path))
        {
            throw new IOException($"配置快照元数据损坏，已保留现场：{path}");
        }
        Save(scriptId, userKey, FromMark(mark, existing));
    }

    public static ConfigStoreMetadata Clone(ConfigStoreMetadata source)
    {
        return new ConfigStoreMetadata
        {
            SchemaVersion = source.SchemaVersion,
            Generation = source.Generation,
            LastCommittedTransactionId = source.LastCommittedTransactionId,
            PluginName = source.PluginName,
            PluginVersion = source.PluginVersion,
            ProfileHash = source.ProfileHash,
            ConfigLocatorHash = source.ConfigLocatorHash,
            ConfigKind = source.ConfigKind,
            UpdatedAt = source.UpdatedAt,
        };
    }

    public bool Matches(ConfigStoreMetadata expected)
    {
        return string.Equals(ConfigLocatorHash, expected.ConfigLocatorHash, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ConfigKind, expected.ConfigKind, StringComparison.OrdinalIgnoreCase);
    }

    public static string HashLocator(string configPath)
    {
        string normalized;
        try
        {
            normalized = Path.GetFullPath(configPath.Trim());
        }
        catch
        {
            normalized = configPath.Trim();
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    /// <summary>启动时从旧版 store-archive 恢复最近一份可用快照；成功后由新协议首次写入时清理旧归档。</summary>
    public static bool TryRestoreLegacyArchive(string scriptId, string userKey)
    {
        string store = ConfigSwapPaths.StoreDir(scriptId, userKey);
        if (File.Exists(store)
            || (Directory.Exists(store) && Directory.EnumerateFileSystemEntries(store).Any()))
        {
            return false;
        }
        // 仅有一个空 store 目录时，它没有可恢复内容；先移除这个明确的空占位，
        // 让旧归档可以安全提升为当前快照。
        if (Directory.Exists(store))
        {
            Directory.Delete(store);
        }
        string archiveRoot = ConfigSwapPaths.StoreArchiveDir(scriptId, userKey);
        if (!Directory.Exists(archiveRoot))
        {
            return false;
        }

        foreach (string archive in Directory.GetDirectories(archiveRoot).OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            string archivedStore = Path.Combine(archive, "store");
            if ((!Directory.Exists(archivedStore) && !File.Exists(archivedStore))
                || (Directory.Exists(archivedStore) && !Directory.EnumerateFileSystemEntries(archivedStore).Any()))
            {
                continue;
            }
            try
            {
                if (Directory.Exists(archivedStore))
                {
                    Directory.Move(archivedStore, store);
                }
                else
                {
                    File.Move(archivedStore, store);
                }
                string archivedMetadata = Path.Combine(archive, "store-meta.json");
                if (File.Exists(archivedMetadata) && !File.Exists(ConfigSwapPaths.StoreMetadataPath(scriptId, userKey)))
                {
                    File.Move(archivedMetadata, ConfigSwapPaths.StoreMetadataPath(scriptId, userKey));
                }
                Logger.Info($"[配置快照] 已从旧归档恢复：{archive} → {store}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[配置快照] 旧归档恢复失败，继续检查其他归档（{archive}）：{ex.Message}");
            }
        }
        return false;
    }

    /// <summary>恢复定位重绑定在移动旧快照后中断的现场；新快照已存在时保留隔离区，等待匹配校验后清理。</summary>
    public static void RecoverRebind(string scriptId, string userKey)
    {
        string store = ConfigSwapPaths.StoreDir(scriptId, userKey);
        string rebindDir = ConfigSwapPaths.StoreRebindDir(scriptId, userKey);
        string oldDir = ConfigSwapPaths.StoreRebindOldDir(scriptId, userKey);
        string oldFile = Path.Combine(rebindDir, "old-store");
        if (!Directory.Exists(rebindDir) || !Directory.EnumerateFileSystemEntries(rebindDir).Any())
        {
            return;
        }
        string metadata = ConfigSwapPaths.StoreMetadataPath(scriptId, userKey);
        string oldMetadata = Path.Combine(rebindDir, "old-store-meta.json");
        string pendingMetadata = ConfigSwapPaths.StoreRebindNewMetadataPath(scriptId, userKey);
        bool hasOldSnapshot = Directory.Exists(oldDir) || File.Exists(oldFile);
        try
        {
            if (!hasOldSnapshot)
            {
                string[] residue = Directory.GetFileSystemEntries(rebindDir);
                if (residue.Length == 1
                    && string.Equals(Path.GetFullPath(residue[0]), Path.GetFullPath(pendingMetadata), StringComparison.OrdinalIgnoreCase)
                    && LoadAt(pendingMetadata) is not null)
                {
                    // 尚未移动旧快照，仅留下已写入的 pending 标记：当前 store 仍是旧快照，
                    // 该重绑定尚未开始，清除标记即可继续使用旧快照。
                    ConfigSwapPrimitives.TryDeleteDir(rebindDir);
                    return;
                }
                throw new IOException("配置位置重绑定现场缺少旧快照，保留未知残留");
            }

            bool hasCurrentStore = Directory.Exists(store) || File.Exists(store);
            ConfigStoreMetadata? currentMetadata = LoadAt(metadata);
            ConfigStoreMetadata? pending = LoadAt(pendingMetadata);
            bool committed = hasCurrentStore
                && ((pending is not null && currentMetadata is not null && currentMetadata.Matches(pending))
                    || (pending is null
                        && currentMetadata is not null
                        && (!File.Exists(oldMetadata)
                            || LoadAt(oldMetadata) is ConfigStoreMetadata old && !currentMetadata.Matches(old))));
            if (committed)
            {
                ConfigSwapPrimitives.TryDeleteDir(rebindDir);
                Logger.Info($"[配置快照] 已完成中断的配置位置重绑定：{store}");
                return;
            }

            if (hasCurrentStore)
            {
                ConfigSwapPrimitives.ClearPath(store, PathKindUtil.KindOf(store));
            }
            if (Directory.Exists(oldDir))
            {
                Directory.Move(oldDir, store);
            }
            else if (File.Exists(oldFile))
            {
                File.Move(oldFile, store);
            }
            if (File.Exists(oldMetadata))
            {
                if (File.Exists(metadata))
                {
                    File.Delete(metadata);
                }
                File.Move(oldMetadata, metadata);
            }
            ConfigSwapPrimitives.TryDeleteDir(rebindDir);
            Logger.Warn($"[配置快照] 已恢复中断的定位重绑定旧快照：{store}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[配置快照] 定位重绑定恢复失败，保留现场：{rebindDir}：{ex.Message}");
        }
    }

    public static void CleanupRebindIfMatches(string scriptId, string userKey, ConfigStoreMetadata expected)
    {
        ConfigStoreMetadata? current = Load(scriptId, userKey);
        if (current is not null && current.Matches(expected))
        {
            ConfigSwapPrimitives.TryDeleteDir(ConfigSwapPaths.StoreRebindDir(scriptId, userKey));
        }
    }

    /// <summary>新快照已验证并成功写入后，清理旧版完整副本与旧全量事务残留。</summary>
    public static void CleanupLegacyArtifacts(string scriptId, string userKey)
    {
        string store = ConfigSwapPaths.StoreDir(scriptId, userKey);
        if (!Directory.Exists(store) || !Directory.EnumerateFileSystemEntries(store).Any())
        {
            return;
        }
        foreach (string path in new[]
        {
            ConfigSwapPaths.StoreArchiveDir(scriptId, userKey),
            ConfigSwapPaths.StorePreviousDir(scriptId, userKey),
            ConfigSwapPaths.StoreTempDir(scriptId, userKey),
            ConfigSwapPaths.RetryStoreDir(scriptId, userKey),
        })
        {
            ConfigSwapPrimitives.TryDeleteDir(path);
        }
    }

    /// <summary>配置定位变更时隔离旧快照；新配置成功物化后删除隔离区，失败则恢复旧快照。</summary>
    public static void RebindStore(
        string scriptId,
        string userKey,
        string configPath,
        ConfigStoreMetadata expected)
    {
        string store = ConfigSwapPaths.StoreDir(scriptId, userKey);
        if (!Directory.Exists(store) && !File.Exists(store))
        {
            throw new IOException("待重绑定的配置快照不存在");
        }
        if (PathKindUtil.KindOf(configPath) == PathKind.Missing)
        {
            throw new IOException($"配置路径已变更但新位置不存在：{configPath}");
        }

        string rebindDir = ConfigSwapPaths.StoreRebindDir(scriptId, userKey);
        string oldDir = ConfigSwapPaths.StoreRebindOldDir(scriptId, userKey);
        if (Directory.Exists(rebindDir) && Directory.EnumerateFileSystemEntries(rebindDir).Any())
        {
            throw new IOException($"配置快照重绑定现场未完成，已保留：{rebindDir}");
        }
        Directory.CreateDirectory(rebindDir);
        string metadata = ConfigSwapPaths.StoreMetadataPath(scriptId, userKey);
        string oldMetadata = Path.Combine(rebindDir, "old-store-meta.json");
        string pendingMetadata = ConfigSwapPaths.StoreRebindNewMetadataPath(scriptId, userKey);
        bool movedStore = false;
        bool movedMetadata = false;
        try
        {
            SaveAt(pendingMetadata, expected);
            if (Directory.Exists(store))
            {
                Directory.Move(store, oldDir);
            }
            else
            {
                File.Move(store, Path.Combine(rebindDir, "old-store"));
            }
            movedStore = true;
            if (File.Exists(metadata))
            {
                File.Move(metadata, oldMetadata);
                movedMetadata = true;
            }
            ConfigSwapPrimitives.CopyAs(configPath, store, PathKind.Dir);
            Save(scriptId, userKey, expected);
            ConfigSwapPrimitives.TryDeleteDir(rebindDir);
        }
        catch
        {
            bool rollbackCompleted = false;
            try
            {
                ConfigSwapPrimitives.ClearPath(store, PathKindUtil.KindOf(store));
                if (movedMetadata && File.Exists(oldMetadata))
                {
                    if (File.Exists(metadata))
                    {
                        File.Delete(metadata);
                    }
                    File.Move(oldMetadata, metadata);
                }
                if (movedStore)
                {
                    if (Directory.Exists(oldDir))
                    {
                        Directory.Move(oldDir, store);
                    }
                    else if (File.Exists(Path.Combine(rebindDir, "old-store")))
                    {
                        File.Move(Path.Combine(rebindDir, "old-store"), store);
                    }
                }
                rollbackCompleted = true;
            }
            catch (Exception rollback)
            {
                Logger.Error($"[配置快照] 重绑定失败且旧快照恢复异常，保留现场：{rollback.Message}");
            }
            if (rollbackCompleted)
            {
                ConfigSwapPrimitives.TryDeleteDir(rebindDir);
            }
            throw;
        }
        Logger.Info($"[配置快照] 已按新配置位置重建快照：{configPath} → {store}");
    }

    [Obsolete("v0.13.2 不再创建完整归档；仅保留旧调用的兼容入口")]
    public static string ArchiveStore(string scriptId, string userKey)
    {
        string store = ConfigSwapPaths.StoreDir(scriptId, userKey);
        if (!Directory.Exists(store) && !File.Exists(store))
        {
            return "";
        }
        string archiveRoot = ConfigSwapPaths.StoreArchiveDir(scriptId, userKey);
        string destination = Path.Combine(archiveRoot, $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(archiveRoot);
        Directory.CreateDirectory(destination);
        if (Directory.Exists(store))
        {
            Directory.Move(store, Path.Combine(destination, "store"));
        }
        else
        {
            File.Move(store, Path.Combine(destination, "store"));
        }
        string metadata = ConfigSwapPaths.StoreMetadataPath(scriptId, userKey);
        if (File.Exists(metadata))
        {
            File.Move(metadata, Path.Combine(destination, "store-meta.json"));
        }
        Logger.Info($"配置快照已归档：{store} → {destination}");
        return destination;
    }
}
