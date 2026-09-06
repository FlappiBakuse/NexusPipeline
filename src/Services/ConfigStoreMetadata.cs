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

    private static readonly JsonSerializerOptions CurrentOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private static readonly string[] RequiredProperties =
    [
        nameof(SchemaVersion),
        nameof(Generation),
        nameof(LastCommittedTransactionId),
        nameof(PluginName),
        nameof(PluginVersion),
        nameof(ProfileHash),
        nameof(ConfigLocatorHash),
        nameof(ConfigKind),
        nameof(UpdatedAt),
    ];

    private static readonly HashSet<string> AllowedProperties =
        RequiredProperties.ToHashSet(StringComparer.Ordinal);

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
            return DeserializeCurrent(File.ReadAllText(path), path);
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

    private static void SaveAt(string path, ConfigStoreMetadata metadata)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonUtil.WriteAtomic(path, JsonSerializer.Serialize(metadata, JsonOpts.Indented));
    }

    private static ConfigStoreMetadata? DeserializeCurrent(string json, string path)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("配置快照元数据根节点必须是对象");
        }
        var properties = document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (RequiredProperties.Any(property => !properties.Contains(property))
            || properties.Any(property => !AllowedProperties.Contains(property)))
        {
            throw new InvalidDataException("配置快照元数据不是当前格式");
        }
        ConfigStoreMetadata? metadata = JsonSerializer.Deserialize<ConfigStoreMetadata>(json, CurrentOptions);
        if (metadata is null
            || metadata.SchemaVersion != CurrentSchemaVersion
            || metadata.ConfigKind is not ("missing" or "file" or "dir"))
        {
            throw new InvalidDataException($"配置快照元数据版本或字段无效：{path}");
        }
        return metadata;
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
            mark.ConfigKind));
        if (existing is not null)
        {
            expected.Generation = existing.Generation;
            expected.LastCommittedTransactionId = existing.LastCommittedTransactionId;
            expected.SchemaVersion = CurrentSchemaVersion;
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

}
