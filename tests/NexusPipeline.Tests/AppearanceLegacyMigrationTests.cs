using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Persistence;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Plugins;
using NexusPipeline.Plugins.Managed;
using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>宿主旧外观现场的一次性格式搬迁：载荷结构、Id 重映射、幂等与中断重试。</summary>
public sealed class AppearanceLegacyMigrationTests
{
    private const string FirstOldId = "11111111111111111111111111111111";
    private const string SecondOldId = "22222222222222222222222222222222";
    private const string ThirdOldId = "33333333333333333333333333333333";
    private const string CurrentOldId = "44444444444444444444444444444444";

    [Fact]
    public void Run_MigratesLegacyConfigAssetsAndRotationCursor()
    {
        using var fixture = new MigrationFixture();
        byte[] firstBytes = ImageBytes("first-wallpaper");
        byte[] secondBytes = ImageBytes("second-wallpaper");
        fixture.WriteAssetFile(FirstOldId, ".jpg", firstBytes);
        fixture.WriteAssetFile(SecondOldId, ".png", secondBytes);
        fixture.WriteConfig(new JsonObject
        {
            ["schemaVersion"] = 1,
            ["provider"] = new JsonObject { ["pluginName"] = fixture.PluginName, ["enabled"] = true },
            ["assets"] = new JsonArray(
                AssetNode(FirstOldId, "a.jpg", "image/jpeg", "2026-01-02T03:04:05+00:00", new JsonObject { ["--accent"] = "#123456" }, 3),
                AssetNode(SecondOldId, "b.png", "image/png", "2026-02-03T04:05:06+00:00", new JsonObject(), 0)),
            ["order"] = new JsonArray(FirstOldId, SecondOldId),
            ["selectedId"] = SecondOldId,
            ["rotation"] = new JsonObject { ["mode"] = "timer", ["intervalMinutes"] = 45, ["epochUnixMs"] = 1_700_000_000_000 },
            ["effects"] = new JsonObject
            {
                ["blurPx"] = 8,
                ["dimPercent"] = 25,
                ["surfaceTransparencyPercent"] = 12,
                ["applyTransparencyToSecondarySurfaces"] = false,
            },
        });
        fixture.WriteRuntimeState(new JsonObject
        {
            ["lastRandomId"] = FirstOldId,
            ["timerSlot"] = 3,
            ["timerEpochUnixMs"] = 1_700_000_000_000,
            ["timerIntervalMinutes"] = 45,
        });

        Assert.True(fixture.Migration().Run());

        JsonObject payload = fixture.ReadPayload();
        Assert.Equal(1, payload["schemaVersion"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(payload["migratedAt"]!.GetValue<string>()));
        JsonObject settings = payload["settings"]!.AsObject();
        JsonArray assets = payload["assets"]!.AsArray();
        Assert.Equal(2, assets.Count);

        string expectedFirstId = Convert.ToHexString(SHA256.HashData(firstBytes)).ToLowerInvariant();
        string expectedSecondId = Convert.ToHexString(SHA256.HashData(secondBytes)).ToLowerInvariant();
        Assert.Equal(new[] { expectedFirstId, expectedSecondId }, settings["order"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
        Assert.Equal(expectedSecondId, settings["selectedId"]!.GetValue<string>());
        Assert.Equal(expectedFirstId, settings["currentId"]!.GetValue<string>());
        Assert.True(settings["providerEnabled"]!.GetValue<bool>());
        Assert.Equal("timer", settings["rotation"]!["mode"]!.GetValue<string>());
        Assert.Equal(45, settings["rotation"]!["intervalMinutes"]!.GetValue<int>());
        Assert.Equal(1_700_000_000_000, settings["rotation"]!["epochUnixMs"]!.GetValue<long>());
        Assert.Equal(8, settings["effects"]!["blurPx"]!.GetValue<int>());
        Assert.Equal(25, settings["effects"]!["dimPercent"]!.GetValue<int>());
        Assert.Equal(12, settings["effects"]!["surfaceTransparencyPercent"]!.GetValue<int>());
        Assert.False(settings["effects"]!["applyTransparencyToSecondarySurfaces"]!.GetValue<bool>());

        JsonObject first = assets[0]!.AsObject();
        Assert.Equal(expectedFirstId, first["id"]!.GetValue<string>());
        Assert.Equal("jpg", first["extension"]!.GetValue<string>());
        Assert.Equal("a.jpg", first["originalName"]!.GetValue<string>());
        Assert.Equal("image/jpeg", first["mimeType"]!.GetValue<string>());
        Assert.Equal(firstBytes.Length, first["sizeBytes"]!.GetValue<long>());
        Assert.Equal(
            DateTimeOffset.Parse("2026-01-02T03:04:05+00:00", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(first["createdAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("#123456", first["palette"]!["--accent"]!.GetValue<string>());
        Assert.Equal(3, first["paletteVersion"]!.GetValue<int>());
        Assert.Equal("png", assets[1]!["extension"]!.GetValue<string>());

        Assert.True(File.Exists(fixture.AssetFilePath(fixture.PluginName, expectedFirstId, "jpg")));
        Assert.True(File.Exists(fixture.AssetFilePath(fixture.PluginName, expectedSecondId, "png")));
        Assert.True(fixture.AssetFilePath(FirstOldId, ".jpg", fixture.AssetsDir).Length > 0);
        Assert.True(File.Exists(fixture.LegacyAssetFile(FirstOldId, ".jpg")));
        Assert.True(File.Exists(fixture.LegacyAssetFile(SecondOldId, ".png")));
        Assert.True(File.Exists(fixture.ConfigPath));
        Assert.True(File.Exists(fixture.RuntimePath));

        JsonObject marker = fixture.ReadMarker();
        Assert.Equal(1, marker["schemaVersion"]!.GetValue<int>());
        Assert.Equal("completed", marker["status"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(marker["reason"]!.GetValue<string>()));
    }

    [Fact]
    public void Run_SkipsAssetsWithoutLegacyFileAndRemapsTheRemainingIds()
    {
        using var fixture = new MigrationFixture();
        byte[] bytes = ImageBytes("kept-wallpaper");
        fixture.WriteAssetFile(FirstOldId, ".png", bytes);
        fixture.WriteConfig(new JsonObject
        {
            ["provider"] = new JsonObject { ["pluginName"] = fixture.PluginName, ["enabled"] = true },
            ["assets"] = new JsonArray(
                AssetNode(FirstOldId, "kept.png", "image/png", null, new JsonObject(), 0),
                AssetNode(ThirdOldId, "missing.png", "image/png", null, new JsonObject(), 0)),
            ["order"] = new JsonArray(ThirdOldId, FirstOldId),
            ["selectedId"] = ThirdOldId,
        });

        Assert.True(fixture.Migration().Run());

        JsonObject settings = fixture.ReadPayload()["settings"]!.AsObject();
        string expectedId = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.Equal(new[] { expectedId }, settings["order"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
        Assert.Equal(expectedId, settings["selectedId"]!.GetValue<string>());
        Assert.Single(fixture.ReadPayload()["assets"]!.AsArray());
        Assert.Contains("1/2", fixture.ReadMarker()["reason"]!.GetValue<string>());
        Assert.False(File.Exists(fixture.AssetFilePath(fixture.PluginName, ThirdOldId, "png")));
    }

    [Fact]
    public void Run_IsIdempotentAndDoesNotDuplicateAssetsOrPayload()
    {
        using var fixture = new MigrationFixture();
        byte[] bytes = ImageBytes("stable-wallpaper");
        fixture.WriteAssetFile(FirstOldId, ".png", bytes);
        fixture.WriteConfig(new JsonObject
        {
            ["provider"] = new JsonObject { ["pluginName"] = fixture.PluginName, ["enabled"] = true },
            ["assets"] = new JsonArray(AssetNode(FirstOldId, "a.png", "image/png", null, new JsonObject(), 0)),
            ["order"] = new JsonArray(FirstOldId),
            ["selectedId"] = FirstOldId,
        });

        Assert.True(fixture.Migration().Run());
        string payloadBefore = fixture.ReadPayloadText();
        string markerBefore = fixture.ReadMarkerText();
        int importsAfterFirstRun = fixture.ImportCount;
        Assert.Equal(1, importsAfterFirstRun);

        Assert.False(fixture.Migration().Run());

        Assert.Equal(payloadBefore, fixture.ReadPayloadText());
        Assert.Equal(markerBefore, fixture.ReadMarkerText());
        Assert.Equal(importsAfterFirstRun, fixture.ImportCount);
        Assert.Single(fixture.ReadPayload()["assets"]!.AsArray());
        Assert.Single(Directory.GetFiles(fixture.AssetDir(fixture.PluginName, "wallpapers")));
    }

    [Fact]
    public void Run_WithoutLegacyData_WritesSkippedMarkerAndReportsNoError()
    {
        using var fixture = new MigrationFixture();

        Assert.False(fixture.Migration().Run());

        JsonObject marker = fixture.ReadMarker();
        Assert.Equal("skipped", marker["status"]!.GetValue<string>());
        Assert.Contains("没有旧外观配置", marker["reason"]!.GetValue<string>());
        Assert.Equal(0, fixture.ImportCount);
        Assert.False(Directory.Exists(fixture.AssetDir(fixture.PluginName, "wallpapers")));
    }

    [Fact]
    public void Run_WithoutProviderPluginName_SkipsMigrationAndRecordsReason()
    {
        using var fixture = new MigrationFixture();
        fixture.WriteAssetFile(FirstOldId, ".png", ImageBytes("orphan-wallpaper"));
        fixture.WriteConfig(new JsonObject
        {
            ["assets"] = new JsonArray(AssetNode(FirstOldId, "a.png", "image/png", null, new JsonObject(), 0)),
            ["order"] = new JsonArray(FirstOldId),
            ["selectedId"] = FirstOldId,
        });

        Assert.False(fixture.Migration().Run());

        JsonObject marker = fixture.ReadMarker();
        Assert.Equal("skipped", marker["status"]!.GetValue<string>());
        Assert.Contains("提供方插件名", marker["reason"]!.GetValue<string>());
        Assert.Equal(0, fixture.ImportCount);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "plugins")));
        Assert.True(File.Exists(fixture.LegacyAssetFile(FirstOldId, ".png")));
    }

    [Fact]
    public void Run_RetriesAfterImportInterruptionWithoutDuplicatingAssets()
    {
        using var fixture = new MigrationFixture();
        byte[] firstBytes = ImageBytes("retry-first");
        byte[] secondBytes = ImageBytes("retry-second");
        fixture.WriteAssetFile(FirstOldId, ".jpg", firstBytes);
        fixture.WriteAssetFile(SecondOldId, ".jpg", secondBytes);
        fixture.WriteConfig(new JsonObject
        {
            ["provider"] = new JsonObject { ["pluginName"] = fixture.PluginName, ["enabled"] = false },
            ["assets"] = new JsonArray(
                AssetNode(FirstOldId, "a.jpg", "image/jpeg", null, new JsonObject(), 0),
                AssetNode(SecondOldId, "b.jpg", "image/jpeg", null, new JsonObject(), 0)),
            ["order"] = new JsonArray(FirstOldId, SecondOldId),
            ["selectedId"] = FirstOldId,
        });

        var failing = new FailingImportFixture(fixture, failAfter: 1);
        AppearanceLegacyMigration interrupted = failing.Migration();
        Assert.Throws<IOException>(() => interrupted.Run());

        Assert.False(File.Exists(fixture.MarkerPath));
        Assert.False(File.Exists(fixture.PayloadPath(fixture.PluginName)));
        string firstId = Convert.ToHexString(SHA256.HashData(firstBytes)).ToLowerInvariant();
        Assert.True(File.Exists(fixture.AssetFilePath(fixture.PluginName, firstId, "jpg")));
        Assert.Equal(1, fixture.ImportCount);

        Assert.True(fixture.Migration().Run());

        Assert.Equal(3, fixture.ImportCount);
        JsonObject payload = fixture.ReadPayload();
        string secondId = Convert.ToHexString(SHA256.HashData(secondBytes)).ToLowerInvariant();
        Assert.Equal(
            new[] { firstId, secondId },
            payload["settings"]!["order"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
        Assert.Equal(2, payload["assets"]!.AsArray().Count);
        Assert.Equal(2, Directory.GetFiles(fixture.AssetDir(fixture.PluginName, "wallpapers")).Length);
        Assert.Equal("completed", fixture.ReadMarker()["status"]!.GetValue<string>());
    }

    private static JsonObject AssetNode(
        string id,
        string originalName,
        string mimeType,
        string? createdAt,
        JsonObject palette,
        int paletteVersion)
    {
        var node = new JsonObject
        {
            ["id"] = id,
            ["originalName"] = originalName,
            ["mimeType"] = mimeType,
            ["sizeBytes"] = 1,
            ["sha256"] = new string('a', 64),
            ["palette"] = palette,
            ["paletteVersion"] = paletteVersion,
        };
        if (createdAt is not null)
        {
            node["createdAt"] = createdAt;
        }
        return node;
    }

    private static byte[] ImageBytes(string label)
    {
        return Encoding.UTF8.GetBytes("jpeg-bytes:" + label);
    }

    /// <summary>让第 N 次导入抛错，模拟搬迁中途中断；中断只发生在既有实现在失败前会写入的位置。</summary>
    private sealed class FailingImportFixture
    {
        private readonly MigrationFixture _fixture;
        private readonly int _failAfter;
        private int _calls;

        public FailingImportFixture(MigrationFixture fixture, int failAfter)
        {
            _fixture = fixture;
            _failAfter = failAfter;
        }

        public AppearanceLegacyMigration Migration()
        {
            return _fixture.CreateMigration(
                (plugin, extension, source, token) =>
                {
                    _calls += 1;
                    if (_calls > _failAfter)
                    {
                        throw new IOException("中断的搬迁");
                    }
                    return _fixture.Import(plugin, extension, source, token);
                },
                _fixture.WritePayload);
        }
    }

    private sealed class MigrationFixture : IDisposable
    {
        public MigrationFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "nxp-appearance-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            PluginName = "fixture-wallpaper-" + Guid.NewGuid().ToString("N")[..12];
            PluginRoot = Path.Combine(AppPaths.ConfigDir, "plugins", PluginName);
        }

        public string Root { get; }

        /// <summary>每个夹具使用独立的插件命名空间，落在生产路径布局下并在释放时清理。</summary>
        public string PluginName { get; }

        public string PluginRoot { get; }

        public int ImportCount { get; set; }

        public string ConfigPath => Path.Combine(Root, "config", "appearance.json");

        public string AssetsDir => Path.Combine(Root, "user-assets", "appearance", "wallpapers");

        public string RuntimePath => Path.Combine(Root, ".nxp", "state", "appearance-runtime.json");

        public string MarkerPath => Path.Combine(Root, ".nxp", "state", "appearance-migration.json");

        public AppearanceLegacyMigration Migration() => CreateMigration(null, WritePayload);

        public AppearanceLegacyMigration CreateMigration(
            Func<string, string, string, CancellationToken, ValueTask<PluginAssetInfo>>? importAsset,
            Func<string, JsonObject, CancellationToken, ValueTask> writePayload)
        {
            return new AppearanceLegacyMigration(
                configPath: ConfigPath,
                assetsDir: AssetsDir,
                runtimePath: RuntimePath,
                markerPath: MarkerPath,
                importAsset: importAsset ?? Import,
                writePayload: writePayload);
        }

        /// <summary>把旧资产写入插件资产命名空间，并记录调用次数供中断重试用例断言。</summary>
        public ValueTask<PluginAssetInfo> Import(
            string pluginName,
            string extension,
            string sourcePath,
            CancellationToken cancellationToken)
        {
            ImportCount += 1;
            return new PluginAssetStore(pluginName)
                .ImportAsync("wallpapers", extension, sourcePath, cancellationToken);
        }

        public void WriteConfig(JsonObject config)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, config.ToJsonString());
        }

        public void WriteRuntimeState(JsonObject state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RuntimePath)!);
            File.WriteAllText(RuntimePath, state.ToJsonString());
        }

        public void WriteAssetFile(string id, string extension, byte[] bytes)
        {
            Directory.CreateDirectory(AssetsDir);
            File.WriteAllBytes(Path.Combine(AssetsDir, id + extension), bytes);
        }

        public string LegacyAssetFile(string id, string extension) => Path.Combine(AssetsDir, id + extension);

        public string AssetDir(string pluginName, string scope)
        {
            return Path.Combine(AppPaths.ConfigDir, "plugins", pluginName, "assets", scope);
        }

        public string AssetFilePath(string pluginName, string assetId, string extension)
        {
            return Path.Combine(AssetDir(pluginName, "wallpapers"), assetId + "." + extension);
        }

        public string PayloadPath(string pluginName)
        {
            return Path.Combine(AppPaths.ConfigDir, "plugins", pluginName, "scopes", AppearanceLegacyMigration.ImportScope + ".json");
        }

        public JsonObject ReadPayload()
        {
            return JsonNode.Parse(ReadPayloadText())!.AsObject();
        }

        public string ReadPayloadText()
        {
            JsonObject? payload = Store(PluginName)
                .ReadJsonAsync(AppearanceLegacyMigration.ImportScope)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            Assert.NotNull(payload);
            return payload!.ToJsonString();
        }

        public JsonObject ReadMarker()
        {
            return JsonNode.Parse(ReadMarkerText())!.AsObject();
        }

        public string ReadMarkerText() => File.ReadAllText(MarkerPath);

        /// <summary>把载荷写入真实的作用域数据文件；读取路径与生产写入路径保持同一份推导。</summary>
        public ValueTask WritePayload(string pluginName, JsonObject payload, CancellationToken cancellationToken)
        {
            return Store(pluginName).WriteJsonAsync(
                AppearanceLegacyMigration.ImportScope,
                payload,
                cancellationToken);
        }

        private static PluginScopedDataStore Store(string pluginName)
        {
            return new PluginScopedDataStore(pluginName);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
            if (Directory.Exists(PluginRoot))
            {
                Directory.Delete(PluginRoot, recursive: true);
            }
        }
    }
}
