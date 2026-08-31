using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Persistence;
using NexusPipeline.Plugins;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>服务端同步外观配置、壁纸资产和轮换状态。</summary>
internal sealed class AppearanceService
{
    internal const long MaxAssetBytes = 8L * 1024 * 1024;
    internal const long MaxTotalBytes = 256L * 1024 * 1024;
    internal const int MaxAssets = 32;

    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
    };

    private static readonly HashSet<string> AllowedPaletteTokens = new(StringComparer.Ordinal)
    {
        "--bg", "--bg-raised", "--panel", "--panel-solid", "--panel-soft", "--panel-hover",
        "--hover-bg", "--border", "--border-strong", "--text", "--muted", "--faint",
        "--accent", "--accent-strong", "--accent-alt", "--accent-soft", "--on-accent",
        "--ok", "--bad", "--bad-soft", "--bad-border", "--warn", "--ok-soft", "--warn-soft", "--muted-soft",
        "--shadow", "--mask", "--log-bg", "--log-text", "--focus", "--select-arrow",
        "--wallpaper-card-dark", "--wallpaper-card-dark-soft", "--wallpaper-card-dark-hover", "--wallpaper-card-dark-border",
        "--wallpaper-card-light", "--wallpaper-card-light-soft", "--wallpaper-card-light-hover", "--wallpaper-card-light-border",
    };

    private readonly Func<PluginManager>? _plugins;
    private readonly string _configPath;
    private readonly string _runtimePath;
    private readonly string _assetsDir;
    private readonly string _stagingDir;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _sync = new();

    public AppearanceService(
        Func<PluginManager>? plugins = null,
        string? configPath = null,
        string? runtimePath = null,
        string? assetsDir = null,
        string? stagingDir = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _plugins = plugins;
        _configPath = Path.GetFullPath(configPath ?? AppPaths.AppearanceConfigPath);
        _runtimePath = Path.GetFullPath(runtimePath ?? AppPaths.AppearanceRuntimePath);
        _assetsDir = Path.GetFullPath(assetsDir ?? AppPaths.AppearanceAssetsDir);
        _stagingDir = Path.GetFullPath(stagingDir ?? AppPaths.AppearanceStagingDir);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public AppearanceSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            AppearanceConfig config = LoadConfig();
            return BuildSnapshot(config);
        }
    }

    public AppearanceSnapshot Save(JsonObject patch, string caller)
    {
        caller = EnsureWritable(caller, allowClaim: true);
        lock (_sync)
        {
            AppearanceConfig config = LoadConfig();
            EnsureProviderOwnership(config, caller, allowClaim: true);
            ApplyPatch(config, patch, caller);
            config.Revision = Math.Max(1, config.Revision + 1);
            config.UpdatedAt = DateTimeOffset.UtcNow;
            SaveConfig(config);
            return BuildSnapshot(config);
        }
    }

    public AppearanceSnapshot StartStartupRotation(string caller)
    {
        caller = EnsureWritable(caller, allowClaim: true);
        lock (_sync)
        {
            AppearanceConfig config = LoadConfig();
            bool claimedProvider = EnsureProviderOwnership(config, caller, allowClaim: true);
            if (config.Rotation.Mode.Equals("startup", StringComparison.OrdinalIgnoreCase)
                && config.Order.Count > 0)
            {
                AppearanceRuntimeState state = LoadRuntimeState();
                state.LastRandomId = PickRandomId(config.Order, state.LastRandomId);
                SaveRuntimeState(state);
            }
            if (claimedProvider)
            {
                config.Revision = Math.Max(1, config.Revision + 1);
                config.UpdatedAt = DateTimeOffset.UtcNow;
                SaveConfig(config);
            }
            return BuildSnapshot(config);
        }
    }

    public AppearanceSnapshot SavePalette(string caller, string assetId, JsonObject palette)
    {
        caller = EnsureWritable(caller, allowClaim: true);
        lock (_sync)
        {
            AppearanceConfig config = LoadConfig();
            EnsureProviderOwnership(config, caller, allowClaim: true);
            AppearanceAsset asset = FindAsset(config, assetId);
            asset.Palette = ParsePalette(palette);
            asset.PaletteVersion = 3;
            config.Revision = Math.Max(1, config.Revision + 1);
            config.UpdatedAt = DateTimeOffset.UtcNow;
            SaveConfig(config);
            return BuildSnapshot(config);
        }
    }

    public async Task<AppearanceAsset> UploadAsync(
        Stream input,
        string? contentType,
        string? originalName,
        long declaredLength,
        string caller,
        CancellationToken cancellationToken = default)
    {
        caller = EnsureWritable(caller, allowClaim: true);
        string mime = (contentType ?? "").Split(';', 2)[0].Trim().ToLowerInvariant();
        if (!AllowedMimeTypes.Contains(mime))
        {
            throw new AppearanceException("invalid_type", "壁纸仅支持 JPEG、PNG 或 WebP");
        }
        if (declaredLength >= 0 && declaredLength > MaxAssetBytes)
        {
            throw new AppearanceException("too_large", "壁纸文件不能超过 8192 KB");
        }

        Directory.CreateDirectory(_stagingDir);
        string temporaryPath = Path.Combine(_stagingDir, "upload." + Guid.NewGuid().ToString("N") + ".tmp");
        long total = 0;
        byte[] header = new byte[12];
        int headerLength = 0;
        AppearanceAsset? completedAsset = null;
        string sha256 = "";
        try
        {
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[64 * 1024];
                while (true)
                {
                    int read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }
                    total += read;
                    if (total > MaxAssetBytes)
                    {
                        throw new AppearanceException("too_large", "壁纸文件不能超过 8192 KB");
                    }
                    int copy = Math.Min(read, header.Length - headerLength);
                    if (copy > 0)
                    {
                        Buffer.BlockCopy(buffer, 0, header, headerLength, copy);
                        headerLength += copy;
                    }
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            ValidateImageHeader(mime, header, headerLength, total);

            lock (_sync)
            {
                AppearanceConfig config = LoadConfig();
                bool claimedProvider = EnsureProviderOwnership(config, caller, allowClaim: true);
                AppearanceAsset? duplicate = config.Assets.FirstOrDefault(item =>
                    string.Equals(item.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
                if (duplicate is not null)
                {
                    if (claimedProvider)
                    {
                        config.Revision = Math.Max(1, config.Revision + 1);
                        config.UpdatedAt = DateTimeOffset.UtcNow;
                        SaveConfig(config);
                    }
                    completedAsset = duplicate;
                }
                if (completedAsset is null && config.Assets.Count >= MaxAssets)
                {
                    throw new AppearanceException("quota", "壁纸数量不能超过 32 张");
                }
                long existingBytes = config.Assets.Sum(item => item.SizeBytes);
                if (completedAsset is null && existingBytes + total > MaxTotalBytes)
                {
                    throw new AppearanceException("quota", "壁纸总容量不能超过 256 MiB");
                }
                if (completedAsset is null)
                {
                    string id = Guid.NewGuid().ToString("N");
                    string extension = mime switch
                    {
                        "image/jpeg" => ".jpg",
                        "image/png" => ".png",
                        _ => ".webp",
                    };
                    Directory.CreateDirectory(_assetsDir);
                    string destination = Path.Combine(_assetsDir, id + extension);
                    EnsureChildPath(_assetsDir, destination);
                    File.Move(temporaryPath, destination);
                    var asset = new AppearanceAsset
                    {
                        Id = id,
                        OriginalName = SanitizeFileName(originalName, id + extension),
                        MimeType = mime,
                        SizeBytes = total,
                        Sha256 = sha256,
                        CreatedAt = DateTimeOffset.UtcNow,
                        PaletteVersion = 0,
                        Palette = new Dictionary<string, string>(StringComparer.Ordinal),
                    };
                    config.Assets.Add(asset);
                    config.Order.Add(id);
                    config.SelectedId ??= id;
                    config.Revision = Math.Max(1, config.Revision + 1);
                    config.UpdatedAt = DateTimeOffset.UtcNow;
                    try
                    {
                        SaveConfig(config);
                    }
                    catch
                    {
                        DeleteExact(destination);
                        throw;
                    }
                    completedAsset = asset;
                }
            }
            DeleteExact(temporaryPath);
            return completedAsset!;
        }
        catch
        {
            DeleteExact(temporaryPath);
            throw;
        }
    }

    public AppearanceSnapshot Delete(string caller, string assetId)
    {
        caller = EnsureWritable(caller, allowClaim: true);
        lock (_sync)
        {
            AppearanceConfig config = LoadConfig();
            EnsureProviderOwnership(config, caller, allowClaim: true);
            AppearanceAsset asset = FindAsset(config, assetId);
            string? file = FindAssetPath(asset);
            config.Assets.Remove(asset);
            config.Order.RemoveAll(id => string.Equals(id, asset.Id, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(config.SelectedId, asset.Id, StringComparison.OrdinalIgnoreCase))
            {
                config.SelectedId = config.Order.FirstOrDefault();
            }
            config.Revision = Math.Max(1, config.Revision + 1);
            config.UpdatedAt = DateTimeOffset.UtcNow;
            SaveConfig(config);
            if (file is not null)
            {
                DeleteExact(file);
            }
            return BuildSnapshot(config);
        }
    }

    public bool TryGetAssetPath(string assetId, out string? path, out string mimeType)
    {
        path = null;
        mimeType = "application/octet-stream";
        lock (_sync)
        {
            AppearanceConfig config = LoadConfig();
            AppearanceAsset? asset = config.Assets.FirstOrDefault(item =>
                string.Equals(item.Id, assetId, StringComparison.OrdinalIgnoreCase));
            if (asset is null)
            {
                return false;
            }
            string? candidate = FindAssetPath(asset);
            if (candidate is null || !File.Exists(candidate))
            {
                return false;
            }
            path = candidate;
            mimeType = asset.MimeType;
            return true;
        }
    }

    private AppearanceSnapshot BuildSnapshot(AppearanceConfig config)
    {
        bool effective = IsProviderEffective(config.Provider);
        DateTimeOffset now = _utcNow();
        DateTimeOffset? nextSwitchAt = null;
        string currentId = "";
        if (config.Order.Count > 0)
        {
            if (config.Rotation.Mode.Equals("timer", StringComparison.OrdinalIgnoreCase))
            {
                long intervalMs = Math.Max(1, config.Rotation.IntervalMinutes) * 60_000L;
                long epoch = config.Rotation.EpochUnixMs <= 0 ? now.ToUnixTimeMilliseconds() : config.Rotation.EpochUnixMs;
                long nowMs = now.ToUnixTimeMilliseconds();
                long elapsed = nowMs >= epoch ? nowMs - epoch : 0;
                long slot = elapsed / intervalMs;
                AppearanceRuntimeState runtime = LoadRuntimeState();
                bool sameSlot = runtime.TimerSlot == slot
                    && runtime.TimerEpochUnixMs == epoch
                    && runtime.TimerIntervalMinutes == config.Rotation.IntervalMinutes;
                if (!sameSlot || !ContainsId(config.Order, runtime.LastRandomId))
                {
                    runtime.LastRandomId = PickRandomId(config.Order, runtime.LastRandomId);
                    runtime.TimerSlot = slot;
                    runtime.TimerEpochUnixMs = epoch;
                    runtime.TimerIntervalMinutes = config.Rotation.IntervalMinutes;
                    SaveRuntimeState(runtime);
                }
                currentId = runtime.LastRandomId;
                nextSwitchAt = DateTimeOffset.FromUnixTimeMilliseconds(epoch + (slot + 1) * intervalMs);
            }
            else if (config.Rotation.Mode.Equals("startup", StringComparison.OrdinalIgnoreCase))
            {
                AppearanceRuntimeState runtime = LoadRuntimeState();
                currentId = ContainsId(config.Order, runtime.LastRandomId)
                    ? runtime.LastRandomId
                    : ResolveSelectedId(config);
            }
            else
            {
                currentId = ResolveSelectedId(config);
            }
        }
        return new AppearanceSnapshot
        {
            SchemaVersion = config.SchemaVersion,
            Revision = config.Revision,
            EffectiveEnabled = effective && config.Provider.Enabled && config.Order.Count > 0,
            Provider = config.Provider,
            Assets = config.Assets.Select(CloneAsset).ToArray(),
            Order = config.Order.ToArray(),
            SelectedId = config.SelectedId ?? "",
            CurrentId = currentId,
            Rotation = config.Rotation,
            Effects = config.Effects,
            NextSwitchAt = nextSwitchAt,
        };
    }

    private static string ResolveSelectedId(AppearanceConfig config)
    {
        return config.Order.FirstOrDefault(id =>
            string.Equals(id, config.SelectedId, StringComparison.OrdinalIgnoreCase))
            ?? config.Order[0];
    }

    private static bool ContainsId(IReadOnlyList<string> order, string? id)
    {
        return !string.IsNullOrWhiteSpace(id)
            && order.Any(value => string.Equals(value, id, StringComparison.OrdinalIgnoreCase));
    }

    internal static string PickRandomId(IReadOnlyList<string> order, string? previousId)
    {
        if (order.Count == 0)
        {
            return "";
        }
        var candidates = order
            .Where(id => !string.Equals(id, previousId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length == 0)
        {
            return order[0];
        }
        return candidates[RandomNumberGenerator.GetInt32(candidates.Length)];
    }

    private void ApplyPatch(AppearanceConfig config, JsonObject patch, string caller)
    {
        if (patch["provider"] is JsonObject provider)
        {
            string pluginName = provider["pluginName"]?.ToString()?.Trim() ?? caller;
            if (!string.Equals(pluginName, caller, StringComparison.OrdinalIgnoreCase))
            {
                throw new AppearanceException("forbidden", "外观配置只能由当前插件修改");
            }
            config.Provider.PluginName = pluginName;
            if (provider["enabled"] is not null)
            {
                config.Provider.Enabled = ReadBool(provider["enabled"], "provider.enabled");
            }
        }
        if (patch["order"] is JsonArray order)
        {
            var values = new List<string>();
            foreach (JsonNode? value in order)
            {
                string id = value?.ToString()?.Trim() ?? "";
                if (!IsSafeAssetId(id) || values.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    throw new AppearanceException("invalid_config", "壁纸顺序包含无效或重复的资源");
                }
                values.Add(id);
            }
            if (values.Any(id => !config.Assets.Any(asset => string.Equals(asset.Id, id, StringComparison.OrdinalIgnoreCase))))
            {
                throw new AppearanceException("invalid_config", "壁纸顺序包含不存在的资源");
            }
            config.Order = values;
        }
        if (patch["selectedId"] is not null)
        {
            string selected = patch["selectedId"]?.ToString()?.Trim() ?? "";
            if (selected.Length > 0 && !config.Order.Any(id => string.Equals(id, selected, StringComparison.OrdinalIgnoreCase)))
            {
                throw new AppearanceException("invalid_config", "当前壁纸资源不存在");
            }
            config.SelectedId = selected;
        }
        if (patch["rotation"] is JsonObject rotation)
        {
            string mode = rotation["mode"]?.ToString()?.Trim().ToLowerInvariant() ?? config.Rotation.Mode;
            if (mode is not ("off" or "timer" or "startup"))
            {
                throw new AppearanceException("invalid_config", "壁纸轮换模式无效");
            }
            config.Rotation.Mode = mode;
            if (rotation["intervalMinutes"] is not null)
            {
                config.Rotation.IntervalMinutes = Math.Clamp(ReadInt(rotation["intervalMinutes"], "rotation.intervalMinutes"), 1, 1440);
            }
            if (rotation["epochUnixMs"] is not null)
            {
                config.Rotation.EpochUnixMs = Math.Max(0, ReadLong(rotation["epochUnixMs"], "rotation.epochUnixMs"));
            }
            if (config.Rotation.EpochUnixMs <= 0)
            {
                config.Rotation.EpochUnixMs = _utcNow().ToUnixTimeMilliseconds();
            }
        }
        if (patch["effects"] is JsonObject effects)
        {
            if (effects["blurPx"] is not null)
            {
                config.Effects.BlurPx = Math.Clamp(ReadInt(effects["blurPx"], "effects.blurPx"), 0, 40);
            }
            if (effects["dimPercent"] is not null)
            {
                config.Effects.DimPercent = Math.Clamp(ReadInt(effects["dimPercent"], "effects.dimPercent"), 0, 80);
            }
            if (effects["surfaceTransparencyPercent"] is not null)
            {
                config.Effects.SurfaceTransparencyPercent = Math.Clamp(
                    ReadInt(effects["surfaceTransparencyPercent"], "effects.surfaceTransparencyPercent"),
                    0,
                    50);
            }
            if (effects["applyTransparencyToSecondarySurfaces"] is not null)
            {
                config.Effects.ApplyTransparencyToSecondarySurfaces = ReadBool(
                    effects["applyTransparencyToSecondarySurfaces"],
                    "effects.applyTransparencyToSecondarySurfaces");
            }
        }
        if (patch["paletteByAsset"] is JsonObject palettes)
        {
            foreach ((string assetId, JsonNode? node) in palettes)
            {
                if (node is not JsonObject palette)
                {
                    throw new AppearanceException("invalid_config", "壁纸配色格式无效");
                }
                FindAsset(config, assetId).Palette = ParsePalette(palette);
                FindAsset(config, assetId).PaletteVersion = 3;
            }
        }
        config.Order = config.Order
            .Where(id => config.Assets.Any(asset => string.Equals(asset.Id, id, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (string.IsNullOrWhiteSpace(config.SelectedId) || !config.Order.Contains(config.SelectedId, StringComparer.OrdinalIgnoreCase))
        {
            config.SelectedId = config.Order.FirstOrDefault();
        }
    }

    private bool IsProviderEffective(AppearanceProvider provider)
    {
        if (!provider.Enabled || string.IsNullOrWhiteSpace(provider.PluginName) || _plugins is null)
        {
            return false;
        }
        try
        {
            PluginManager manager = _plugins();
            return manager.IsKnownPlugin(provider.PluginName)
                && manager.IsEnabled(provider.PluginName)
                && manager.HasFrontend(provider.PluginName);
        }
        catch
        {
            return false;
        }
    }

    private string EnsureWritable(string caller, bool allowClaim)
    {
        caller = caller?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(caller) || !IsProviderEffective(new AppearanceProvider { PluginName = caller, Enabled = true }))
        {
            throw new AppearanceException("forbidden", "当前插件没有修改外观的权限");
        }
        lock (_sync)
        {
            AppearanceConfig config = LoadConfig();
            EnsureProviderOwnership(config, caller, allowClaim);
        }
        return caller;
    }

    private static bool EnsureProviderOwnership(AppearanceConfig config, string caller, bool allowClaim)
    {
        string owner = config.Provider.PluginName?.Trim() ?? "";
        if (owner.Length == 0)
        {
            if (!allowClaim)
            {
                throw new AppearanceException("forbidden", "当前插件不是外观配置的提供方");
            }
            config.Provider.PluginName = caller;
            return true;
        }
        if (!string.Equals(owner, caller, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppearanceException("forbidden", "当前插件不是外观配置的提供方");
        }
        return false;
    }

    private AppearanceConfig LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                AppearanceConfig? config = JsonSerializer.Deserialize<AppearanceConfig>(
                    File.ReadAllText(_configPath),
                    JsonOpts.Default);
                if (config is not null)
                {
                    Normalize(config);
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[外观] 读取配置失败，将使用默认配置：{ex.Message}");
        }
        return new AppearanceConfig();
    }

    private void SaveConfig(AppearanceConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        JsonUtil.WriteAtomic(_configPath, JsonSerializer.Serialize(config, JsonOpts.Indented));
    }

    private AppearanceRuntimeState LoadRuntimeState()
    {
        try
        {
            if (File.Exists(_runtimePath))
            {
                AppearanceRuntimeState? state = JsonSerializer.Deserialize<AppearanceRuntimeState>(
                    File.ReadAllText(_runtimePath), JsonOpts.Default);
                if (state is not null)
                {
                    return state;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[外观] 读取轮换状态失败：{ex.Message}");
        }
        return new AppearanceRuntimeState();
    }

    private void SaveRuntimeState(AppearanceRuntimeState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_runtimePath)!);
        JsonUtil.WriteAtomic(_runtimePath, JsonSerializer.Serialize(state, JsonOpts.Indented));
    }

    private static void Normalize(AppearanceConfig config)
    {
        config.SchemaVersion = 1;
        config.Revision = Math.Max(1, config.Revision);
        config.Provider ??= new AppearanceProvider();
        config.Provider.PluginName = config.Provider.PluginName?.Trim() ?? "";
        config.Assets ??= new List<AppearanceAsset>();
        config.Assets = config.Assets
            .Where(asset => asset is not null && IsSafeAssetId(asset.Id) && AllowedMimeTypes.Contains(asset.MimeType))
            .Take(MaxAssets)
            .Select(CloneAsset)
            .ToList();
        var assetIds = config.Assets.Select(asset => asset.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        config.Order ??= new List<string>();
        config.Order = config.Order
            .Where(assetIds.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        config.Order.AddRange(config.Assets.Select(asset => asset.Id).Where(id => !config.Order.Contains(id, StringComparer.OrdinalIgnoreCase)));
        config.SelectedId = config.Order.Contains(config.SelectedId ?? "", StringComparer.OrdinalIgnoreCase)
            ? config.SelectedId
            : config.Order.FirstOrDefault();
        config.Rotation ??= new AppearanceRotation();
        string rotationMode = config.Rotation.Mode?.Trim().ToLowerInvariant() ?? "off";
        config.Rotation.Mode = rotationMode is "timer" or "startup"
            ? rotationMode
            : "off";
        config.Rotation.IntervalMinutes = Math.Clamp(config.Rotation.IntervalMinutes, 1, 1440);
        config.Rotation.EpochUnixMs = config.Rotation.EpochUnixMs <= 0
            ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            : Math.Min(config.Rotation.EpochUnixMs, DateTimeOffset.MaxValue.ToUnixTimeMilliseconds());
        config.Effects ??= new AppearanceEffects();
        config.Effects.BlurPx = Math.Clamp(config.Effects.BlurPx, 0, 40);
        config.Effects.DimPercent = Math.Clamp(config.Effects.DimPercent, 0, 80);
        config.Effects.SurfaceTransparencyPercent = Math.Clamp(config.Effects.SurfaceTransparencyPercent, 0, 50);
    }

    private static AppearanceAsset CloneAsset(AppearanceAsset source)
    {
        return new AppearanceAsset
        {
            Id = source.Id,
            OriginalName = source.OriginalName ?? source.Id,
            MimeType = source.MimeType,
            SizeBytes = Math.Max(0, source.SizeBytes),
            Sha256 = source.Sha256 ?? "",
            CreatedAt = source.CreatedAt,
            PaletteVersion = source.PaletteVersion >= 3 ? 3 : source.PaletteVersion == 2 ? 2 : source.PaletteVersion == 1 ? 1 : 0,
            Palette = source.Palette is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : ParsePalette(source.Palette),
        };
    }

    private static Dictionary<string, string> ParsePalette(JsonObject palette)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, JsonNode? value) in palette)
        {
            string text = value?.ToString() ?? "";
            if (!AllowedPaletteTokens.Contains(key) || text.Length > 4096 || text.Any(ch => ch is '\0' or '\r' or '\n'))
            {
                throw new AppearanceException("invalid_palette", $"壁纸配色 token 无效：{key}");
            }
            result[key] = text;
        }
        return result;
    }

    private static Dictionary<string, string> ParsePalette(Dictionary<string, string> palette)
    {
        var objectNode = new JsonObject();
        foreach ((string key, string value) in palette)
        {
            objectNode[key] = value;
        }
        return ParsePalette(objectNode);
    }

    private AppearanceAsset FindAsset(AppearanceConfig config, string id)
    {
        if (!IsSafeAssetId(id))
        {
            throw new AppearanceException("invalid_asset", "壁纸资源 ID 无效");
        }
        return config.Assets.FirstOrDefault(asset => string.Equals(asset.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new AppearanceException("not_found", "壁纸资源不存在");
    }

    private string? FindAssetPath(AppearanceAsset asset)
    {
        string extension = asset.MimeType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => "",
        };
        if (extension.Length == 0 || !IsSafeAssetId(asset.Id))
        {
            return null;
        }
        string path = Path.Combine(_assetsDir, asset.Id + extension);
        EnsureChildPath(_assetsDir, path);
        return path;
    }

    private static void ValidateImageHeader(string mime, byte[] header, int length, long size)
    {
        bool valid = mime switch
        {
            "image/jpeg" => length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            "image/png" => length >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            "image/webp" => length >= 12
                && header.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                && header.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false,
        };
        if (!valid || size <= 0)
        {
            throw new AppearanceException("invalid_image", "壁纸文件内容与声明类型不匹配");
        }
    }

    private static string SanitizeFileName(string? value, string fallback)
    {
        string text = Path.GetFileName(value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text)) text = fallback;
        text = new string(text.Select(ch => char.IsControl(ch) || ch is '/' or '\\' ? '_' : ch).ToArray());
        return text.Length <= 128 ? text : text[..128];
    }

    private static bool IsSafeAssetId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length == 32
        && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static bool ReadBool(JsonNode? node, string field)
    {
        try { return node?.GetValue<bool>() ?? throw new AppearanceException("invalid_config", $"{field} 无效"); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException) { throw new AppearanceException("invalid_config", $"{field} 无效"); }
    }

    private static int ReadInt(JsonNode? node, string field)
    {
        try { return node?.GetValue<int>() ?? throw new AppearanceException("invalid_config", $"{field} 无效"); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException) { throw new AppearanceException("invalid_config", $"{field} 无效"); }
    }

    private static long ReadLong(JsonNode? node, string field)
    {
        try { return node?.GetValue<long>() ?? throw new AppearanceException("invalid_config", $"{field} 无效"); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException) { throw new AppearanceException("invalid_config", $"{field} 无效"); }
    }

    private static void EnsureChildPath(string root, string path)
    {
        string rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"外观资产路径越界：{path}");
        }
    }

    private static void DeleteExact(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[外观] 清理临时资产失败：{ex.Message}");
        }
    }
}

internal sealed class AppearanceConfig
{
    public int SchemaVersion { get; set; } = 1;
    public long Revision { get; set; } = 1;
    public AppearanceProvider Provider { get; set; } = new();
    public List<AppearanceAsset> Assets { get; set; } = new();
    public List<string> Order { get; set; } = new();
    public string? SelectedId { get; set; }
    public AppearanceRotation Rotation { get; set; } = new();
    public AppearanceEffects Effects { get; set; } = new();
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed class AppearanceProvider
{
    public string PluginName { get; set; } = "";
    public bool Enabled { get; set; }
}

internal sealed class AppearanceAsset
{
    public string Id { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string MimeType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int PaletteVersion { get; set; }
    public Dictionary<string, string> Palette { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class AppearanceRotation
{
    public string Mode { get; set; } = "off";
    public int IntervalMinutes { get; set; } = 30;
    public long EpochUnixMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

internal sealed class AppearanceEffects
{
    public int BlurPx { get; set; }
    public int DimPercent { get; set; } = 20;
    public int SurfaceTransparencyPercent { get; set; }

    public bool ApplyTransparencyToSecondarySurfaces { get; set; } = true;
}

internal sealed class AppearanceRuntimeState
{
    public string LastRandomId { get; set; } = "";
    public long TimerSlot { get; set; } = -1;
    public long TimerEpochUnixMs { get; set; }
    public int TimerIntervalMinutes { get; set; }

}

internal sealed class AppearanceSnapshot
{
    public int SchemaVersion { get; init; }
    public long Revision { get; init; }
    public bool EffectiveEnabled { get; init; }
    public AppearanceProvider Provider { get; init; } = new();
    public IReadOnlyList<AppearanceAsset> Assets { get; init; } = Array.Empty<AppearanceAsset>();
    public IReadOnlyList<string> Order { get; init; } = Array.Empty<string>();
    public string SelectedId { get; init; } = "";
    public string CurrentId { get; init; } = "";
    public AppearanceRotation Rotation { get; init; } = new();
    public AppearanceEffects Effects { get; init; } = new();
    public DateTimeOffset? NextSwitchAt { get; init; }
}

internal sealed class AppearanceException : Exception
{
    public AppearanceException(string code, string message) : base(message) { Code = code; }
    public string Code { get; }
}
