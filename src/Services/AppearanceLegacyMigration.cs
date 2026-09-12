using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Persistence;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Plugins;
using NexusPipeline.Plugins.Managed;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>
/// 宿主外观数据的一次性格式搬迁：把 config/appearance.json、user-assets/appearance/wallpapers
/// 与 .nxp/state/appearance-runtime.json 中的旧外观现场交给原提供方插件的资产命名空间与作用域数据。
/// 搬迁只做格式转换；轮换、配额、提供方归属等运行语义由插件自行承担。
/// </summary>
internal sealed class AppearanceLegacyMigration
{
    /// <summary>搬迁载荷 schema 版本；标记文件独立使用同一版本号。</summary>
    internal const int PayloadSchemaVersion = 1;

    /// <summary>搬迁载荷写入的作用域名。</summary>
    internal const string ImportScope = "legacy-appearance-import";

    /// <summary>搬迁载荷中资产所处的资产 scope。</summary>
    internal const string AssetScope = "wallpapers";

    private const string CompletedStatus = "completed";

    private const string SkippedStatus = "skipped";

    private readonly string _configPath;

    private readonly string _runtimePath;

    private readonly string _assetsDir;

    private readonly string _markerPath;

    private readonly Func<string, string, string, CancellationToken, ValueTask<PluginAssetInfo>> _importAsset;

    private readonly Func<string, JsonObject, CancellationToken, ValueTask> _writePayload;

    private readonly Func<DateTimeOffset> _utcNow;

    public AppearanceLegacyMigration(
        string? configPath = null,
        string? assetsDir = null,
        string? runtimePath = null,
        string? markerPath = null,
        Func<string, string, string, CancellationToken, ValueTask<PluginAssetInfo>>? importAsset = null,
        Func<string, JsonObject, CancellationToken, ValueTask>? writePayload = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _configPath = Path.GetFullPath(configPath ?? AppPaths.AppearanceConfigPath);
        _runtimePath = Path.GetFullPath(runtimePath ?? AppPaths.AppearanceRuntimePath);
        _assetsDir = Path.GetFullPath(assetsDir ?? AppPaths.AppearanceAssetsDir);
        _markerPath = Path.GetFullPath(markerPath ?? Path.Combine(AppPaths.StateDir, "appearance-migration.json"));
        _importAsset = importAsset ?? ImportIntoAssetStore;
        _writePayload = writePayload ?? WriteIntoScopedData;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>启动时执行一次搬迁；标记已存在、没有旧数据或缺少提供方插件时只记录标记与日志。</summary>
    public static void ApplyOnce()
    {
        try
        {
            new AppearanceLegacyMigration().Run();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[外观] 旧外观格式搬迁未完成，将在下次启动重试：{ex.Message}");
        }
    }

    /// <summary>
    /// 执行搬迁。成功标记只在资产全部落盘且载荷原子写入成功之后落盘；
    /// 任一步失败都不写标记，下一次启动按相同入口重试，重复写入返回同一内容寻址 Id，因此重试天然幂等。
    /// </summary>
    internal bool Run(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_markerPath))
        {
            return false;
        }
        if (!File.Exists(_configPath))
        {
            WriteMarker(SkippedStatus, "没有旧外观配置");
            return false;
        }

        LegacyAppearanceConfig config;
        try
        {
            config = ReadJson<LegacyAppearanceConfig>(_configPath) ?? new LegacyAppearanceConfig();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[外观] 读取旧外观配置失败：{ex.Message}");
            return false;
        }

        string pluginName = config.Provider?.PluginName?.Trim() ?? "";
        if (!IsSafeProviderName(pluginName))
        {
            string reason = pluginName.Length == 0
                ? "旧外观配置没有提供方插件名"
                : $"旧外观配置的提供方插件名不安全：{pluginName}";
            Logger.Warn($"[外观] 跳过旧外观搬迁：{reason}。");
            WriteMarker(SkippedStatus, reason);
            return false;
        }

        LegacyAppearanceRuntimeState runtime = ReadRuntimeState();
        MigrationResult result = Migrate(config, runtime, pluginName, cancellationToken);
        _writePayload(pluginName, result.Payload, cancellationToken).AsTask().GetAwaiter().GetResult();
        WriteMarker(CompletedStatus, result.Summary);
        Logger.Info($"[外观] 旧外观数据已迁移到插件 {pluginName}：{result.Summary}。");
        return true;
    }

    private MigrationResult Migrate(
        LegacyAppearanceConfig config,
        LegacyAppearanceRuntimeState runtime,
        string pluginName,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LegacyAppearanceAsset> assets = config.Assets ?? new List<LegacyAppearanceAsset>();
        var migrated = new Dictionary<string, MigratedAsset>(StringComparer.OrdinalIgnoreCase);
        int missingFiles = 0;
        foreach (LegacyAppearanceAsset asset in assets)
        {
            string oldId = asset.Id?.Trim() ?? "";
            if (oldId.Length == 0 || migrated.ContainsKey(oldId))
            {
                continue;
            }
            string? source = ResolveAssetPath(asset);
            if (source is null)
            {
                missingFiles += 1;
                Logger.Warn($"[外观] 旧资产文件缺失，跳过迁移：{oldId}");
                continue;
            }
            PluginAssetInfo info = _importAsset(pluginName, ExtensionFor(source, asset.MimeType), source, cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            migrated[oldId] = new MigratedAsset(asset, info);
        }

        var order = new List<string>();
        var payloadAssets = new JsonArray();
        foreach (LegacyAppearanceAsset asset in assets)
        {
            string oldId = asset.Id?.Trim() ?? "";
            if (oldId.Length == 0 || !migrated.TryGetValue(oldId, out MigratedAsset? item))
            {
                continue;
            }
            order.Add(item.Info.Id);
            payloadAssets.Add(BuildAssetNode(item));
        }

        string selectedId = migrated.TryGetValue(config.SelectedId?.Trim() ?? "", out MigratedAsset? selected)
            ? selected.Info.Id
            : "";
        if (selectedId.Length == 0 && order.Count > 0)
        {
            selectedId = order[0];
        }
        string currentId = migrated.TryGetValue(runtime.LastRandomId?.Trim() ?? "", out MigratedAsset? current)
            ? current.Info.Id
            : "";

        JsonObject rotation = BuildRotation(config.Rotation);
        var settings = new JsonObject
        {
            ["selectedId"] = selectedId,
            ["order"] = new JsonArray(order.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["rotation"] = rotation,
            ["effects"] = BuildEffects(config.Effects),
            ["providerEnabled"] = config.Provider?.Enabled ?? false,
        };
        if (currentId.Length > 0)
        {
            // 旧实现的轮换游标只是一张“当前壁纸”：timer 由 epoch + interval 派生 slot 后随机，startup 在启动时随机；
            // slot 可从 rotation 重建，随机结果无法重建，因此以独立字段保留旧语义，由插件决定是否沿用该张。
            settings["currentId"] = currentId;
        }
        var payload = new JsonObject
        {
            ["schemaVersion"] = PayloadSchemaVersion,
            ["migratedAt"] = _utcNow().ToString("O"),
            ["settings"] = settings,
            ["assets"] = payloadAssets,
        };
        string summary = $"资产 {order.Count}/{assets.Count}"
            + (missingFiles > 0 ? $"，缺失文件 {missingFiles} 个" : "")
            + $"，轮换模式 {rotation["mode"]!.GetValue<string>()}";
        return new MigrationResult(payload, summary);
    }

    private JsonObject BuildAssetNode(MigratedAsset item)
    {
        var palette = new JsonObject();
        foreach ((string token, string? value) in item.Asset.Palette ?? new Dictionary<string, string>())
        {
            palette[token] = value ?? "";
        }
        return new JsonObject
        {
            ["id"] = item.Info.Id,
            ["extension"] = item.Info.Extension,
            ["originalName"] = string.IsNullOrWhiteSpace(item.Asset.OriginalName)
                ? item.Info.Id + "." + item.Info.Extension
                : item.Asset.OriginalName,
            ["mimeType"] = item.Asset.MimeType ?? "",
            ["sizeBytes"] = item.Info.SizeBytes,
            ["createdAt"] = (item.Asset.CreatedAt ?? _utcNow()).ToString("O"),
            ["palette"] = palette,
            ["paletteVersion"] = item.Asset.PaletteVersion,
        };
    }

    /// <summary>旧 epoch 为 0 或负数时按旧实现的默认值补齐，保证插件拿到可用的轮换基准时间。</summary>
    private JsonObject BuildRotation(LegacyAppearanceRotation? rotation)
    {
        string mode = rotation?.Mode?.Trim().ToLowerInvariant() ?? "";
        if (mode is not ("timer" or "startup"))
        {
            mode = "off";
        }
        long epoch = rotation?.EpochUnixMs ?? 0;
        if (epoch <= 0)
        {
            epoch = _utcNow().ToUnixTimeMilliseconds();
        }
        return new JsonObject
        {
            ["mode"] = mode,
            ["intervalMinutes"] = Math.Clamp(rotation?.IntervalMinutes ?? 30, 1, 1440),
            ["epochUnixMs"] = epoch,
        };
    }

    private static JsonObject BuildEffects(LegacyAppearanceEffects? effects)
    {
        return new JsonObject
        {
            ["blurPx"] = Math.Clamp(effects?.BlurPx ?? 0, 0, 40),
            ["dimPercent"] = Math.Clamp(effects?.DimPercent ?? 20, 0, 80),
            ["surfaceTransparencyPercent"] = Math.Clamp(effects?.SurfaceTransparencyPercent ?? 0, 0, 50),
            ["applyTransparencyToSecondarySurfaces"] = effects?.ApplyTransparencyToSecondarySurfaces ?? true,
        };
    }

    private LegacyAppearanceRuntimeState ReadRuntimeState()
    {
        try
        {
            return File.Exists(_runtimePath)
                ? ReadJson<LegacyAppearanceRuntimeState>(_runtimePath) ?? new LegacyAppearanceRuntimeState()
                : new LegacyAppearanceRuntimeState();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[外观] 读取旧轮换状态失败，游标按缺省处理：{ex.Message}");
            return new LegacyAppearanceRuntimeState();
        }
    }

    /// <summary>旧资产文件名是“配置 Id + 扩展名”；扩展名取真实文件扩展名，缺失时按 MIME 类型推断。</summary>
    private string? ResolveAssetPath(LegacyAppearanceAsset asset)
    {
        if (!Directory.Exists(_assetsDir))
        {
            return null;
        }
        string id = asset.Id?.Trim() ?? "";
        string? matched = null;
        foreach (string path in Directory.EnumerateFiles(_assetsDir, "*", SearchOption.TopDirectoryOnly))
        {
            if (!string.Equals(Path.GetFileNameWithoutExtension(path), id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.Equals(Path.GetExtension(path), MimeExtension(asset.MimeType), StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
            matched ??= path;
        }
        return matched;
    }

    private static string ExtensionFor(string sourcePath, string? mimeType)
    {
        string extension = Path.GetExtension(sourcePath).TrimStart('.');
        return extension.Length > 0 ? extension : MimeExtension(mimeType).TrimStart('.');
    }

    private static string MimeExtension(string? mimeType) => (mimeType ?? "").Trim().ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/jpeg" => ".jpg",
        _ => "",
    };

    private void WriteMarker(string status, string reason)
    {
        var marker = new JsonObject
        {
            ["schemaVersion"] = PayloadSchemaVersion,
            ["status"] = status,
            ["migratedAt"] = _utcNow().ToString("O"),
            ["reason"] = reason,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(_markerPath)!);
        JsonUtil.WriteAtomic(_markerPath, marker.ToJsonString(JsonOpts.Indented));
    }

    private static T? ReadJson<T>(string path)
    {
        string text = File.ReadAllText(path).Replace("\uFEFF", "");
        return JsonSerializer.Deserialize<T>(text, JsonOpts.Default);
    }

    /// <summary>插件机器名校验与作用域段一致；不猜测、不硬编码官方插件名。</summary>
    internal static bool IsSafeProviderName(string? value)
    {
        return PluginScopedDataStore.IsSafeSegment(value, 64);
    }

    private static ValueTask<PluginAssetInfo> ImportIntoAssetStore(
        string pluginName,
        string extension,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        return new PluginAssetStore(pluginName).ImportAsync(AssetScope, extension, sourcePath, cancellationToken);
    }

    private static ValueTask WriteIntoScopedData(string pluginName, JsonObject payload, CancellationToken cancellationToken)
    {
        return new PluginScopedDataStore(pluginName).WriteJsonAsync(ImportScope, payload, cancellationToken);
    }

    private sealed record MigrationResult(JsonObject Payload, string Summary);

    private sealed record MigratedAsset(LegacyAppearanceAsset Asset, PluginAssetInfo Info);

    private sealed class LegacyAppearanceConfig
    {
        public LegacyAppearanceProvider? Provider { get; set; }

        public List<LegacyAppearanceAsset>? Assets { get; set; }

        public string? SelectedId { get; set; }

        public LegacyAppearanceRotation? Rotation { get; set; }

        public LegacyAppearanceEffects? Effects { get; set; }
    }

    private sealed class LegacyAppearanceProvider
    {
        public string? PluginName { get; set; }

        public bool Enabled { get; set; }
    }

    private sealed class LegacyAppearanceAsset
    {
        public string? Id { get; set; }

        public string? OriginalName { get; set; }

        public string? MimeType { get; set; }

        public long SizeBytes { get; set; }

        public string? Sha256 { get; set; }

        public DateTimeOffset? CreatedAt { get; set; }

        public int PaletteVersion { get; set; }

        public Dictionary<string, string>? Palette { get; set; }
    }

    private sealed class LegacyAppearanceRotation
    {
        public string? Mode { get; set; }

        public int IntervalMinutes { get; set; } = 30;

        public long EpochUnixMs { get; set; }
    }

    private sealed class LegacyAppearanceEffects
    {
        public int BlurPx { get; set; }

        public int DimPercent { get; set; } = 20;

        public int SurfaceTransparencyPercent { get; set; }

        public bool ApplyTransparencyToSecondarySurfaces { get; set; } = true;
    }

    private sealed class LegacyAppearanceRuntimeState
    {
        public string? LastRandomId { get; set; }
    }
}
