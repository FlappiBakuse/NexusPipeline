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

    public static void TrySaveFromMark(string scriptId, string userKey, ConfigSessionMark mark)
    {
        try
        {
            Save(scriptId, userKey, For(mark.ConfigPath, new ConfigSessionRuntimeMetadata(
                mark.WorkingDirectory,
                mark.LaunchExe,
                mark.ProcessIdentity,
                mark.ProfileHash,
                mark.PluginName,
                mark.PluginVersion,
                string.IsNullOrWhiteSpace(mark.ConfigKind) ? mark.OriginalKind : mark.ConfigKind)));
        }
        catch (Exception ex)
        {
            Logger.Warn($"[配置快照] 元数据写入失败（脚本 {scriptId} / 用户 {userKey}）：{ex.Message}");
        }
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
