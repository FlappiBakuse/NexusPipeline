using System.Text.Json.Nodes;
using NexusPipeline.Plugin.Abstractions;
using Xunit;
using NexusPipeline.Host.Composition;
using NexusPipeline.Modules.Plugins.Repository;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Shared.Versioning;
using NexusPipeline.Tests.Support;

namespace NexusPipeline.Tests.Plugins;

public sealed class PluginRepositoryCatalogTests
{
    private static string CandidateHostVersion
    {
        get
        {
            Assert.True(NexusVersion.TryParse(UpdateService.CurrentVersion, out NexusVersion current));
            return $"{current.Major}.{current.Minor}.{checked(current.Patch + 1)}";
        }
    }

    [Fact]
    public void TryParse_ValidOfficialCatalog_ReturnsNormalizedEntry()
    {
        Assert.True(PluginRepositoryCatalog.TryParse(CreateCatalog().ToJsonString(), out PluginCatalog? catalog, out string? error), error);
        PluginCatalogEntry entry = Assert.Single(catalog!.Plugins);
        Assert.Equal("bettergi", entry.Name);
        Assert.Equal("data-specialized", entry.Kind);
        Assert.Equal("0.10.8", entry.MinHostVersion);
        Assert.True(PluginRepositoryCatalog.IsCompatible(entry, "0.10.8", out _));
        Assert.False(PluginRepositoryCatalog.IsCompatible(entry, "0.10.7", out string reason));
        Assert.Contains("需要宿主", reason);
    }

    [Fact]
    public void TryParse_Schema2_ParsesArtifactChangelogAndPresentationMetadata()
    {
        JsonObject root = CreateCatalog();
        JsonObject entry = (JsonObject)((JsonArray)root["plugins"]!)[0]!;
        entry["authors"] = new JsonArray(new JsonObject
        {
            ["name"] = "Nexus Team",
            ["url"] = "https://github.com/FlappiBakuse",
        });
        entry["tags"] = new JsonArray("原神", "专项插件");
        entry["homepage"] = "https://github.com/FlappiBakuse/NexusPipeline-Plugins";
        entry["createdAt"] = "2026-08-20";
        entry["hasReadme"] = true;

        Assert.True(PluginRepositoryCatalog.TryParse(root.ToJsonString(), out PluginCatalog? catalog, out string? error), error);
        Assert.Equal(2, catalog!.SchemaVersion);
        PluginCatalogEntry parsed = Assert.Single(catalog.Plugins);
        Assert.Equal("BetterGI", parsed.ArtifactName);
        Assert.Equal("0.1.0", Assert.Single(parsed.Changelog).Version);
        Assert.Equal("Nexus Team", Assert.Single(parsed.Authors).Name);
        Assert.Equal(new[] { "原神", "专项插件" }, parsed.Tags);
        Assert.Equal("2026-08-20", parsed.CreatedAt);
        Assert.Equal("2026-08-28", parsed.UpdatedAt);
        Assert.True(parsed.HasReadme);
    }

    [Fact]
    public void TryParse_RejectsUnsafeHomepageArtifactNameNonSemverAndDuplicateEntries()
    {
        JsonObject unsafeHomepage = CreateCatalog();
        ((JsonObject)((JsonArray)unsafeHomepage["plugins"]!)[0]!)["homepage"] = "javascript:alert(1)";
        Assert.False(PluginRepositoryCatalog.TryParse(unsafeHomepage.ToJsonString(), out _, out string? homepageError));
        Assert.Contains("homepage", homepageError);

        JsonObject badArtifact = CreateCatalog();
        ((JsonObject)((JsonArray)badArtifact["plugins"]!)[0]!)["artifactName"] = "bettergi";
        Assert.False(PluginRepositoryCatalog.TryParse(badArtifact.ToJsonString(), out _, out string? artifactError));
        Assert.Contains("artifactName", artifactError);

        JsonObject badVersion = CreateCatalog();
        ((JsonObject)((JsonArray)badVersion["plugins"]!)[0]!)["version"] = "1.2.x";
        Assert.False(PluginRepositoryCatalog.TryParse(badVersion.ToJsonString(), out _, out string? versionError));
        Assert.Contains("version", versionError);

        JsonObject duplicate = CreateCatalog();
        JsonArray plugins = (JsonArray)duplicate["plugins"]!;
        plugins.Add(((JsonObject)plugins[0]!).DeepClone());
        Assert.False(PluginRepositoryCatalog.TryParse(duplicate.ToJsonString(), out _, out string? duplicateError));
        Assert.Contains("重复", duplicateError);
    }

    [Fact]
    public void ValidatePackageUrl_AcceptsOfficialRawAndRejectsEverythingElse()
    {
        Assert.Null(PluginRepositoryCatalog.ValidatePackageUrl(
            "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/packages/CustomWallpaper/CustomWallpaper-0.1.1.zip"));

        // 非官方 owner、非 main 分支、大小写不符、查询串、路径穿越与版本不符都必须拒绝。
        Assert.NotNull(PluginRepositoryCatalog.ValidatePackageUrl(
            "https://raw.githubusercontent.com/other-owner/NexusPipeline-Plugins/main/packages/CustomWallpaper/CustomWallpaper-0.1.1.zip"));
        Assert.NotNull(PluginRepositoryCatalog.ValidatePackageUrl(
            "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/dev/packages/CustomWallpaper/CustomWallpaper-0.1.1.zip"));
        Assert.NotNull(PluginRepositoryCatalog.ValidatePackageUrl(
            "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/packages/customwallpaper/CustomWallpaper-0.1.1.zip"));
        Assert.NotNull(PluginRepositoryCatalog.ValidatePackageUrl(
            "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/packages/CustomWallpaper/CustomWallpaper-0.1.1.zip?x=1"));
        Assert.NotNull(PluginRepositoryCatalog.ValidatePackageUrl(
            "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/packages/CustomWallpaper/../CustomWallpaper-0.1.1.zip"));
        Assert.NotNull(PluginRepositoryCatalog.ValidatePackageUrl(
            "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/packages/CustomWallpaper/CustomWallpaper-0.1.0.zip",
            artifactName: "CustomWallpaper",
            version: "0.1.1"));
        Assert.NotNull(PluginRepositoryCatalog.ValidatePackageUrl(
            "https://github.com/FlappiBakuse/NexusPipeline-Plugins/releases/download/v0.1.0/sub/bettergi-0.1.0.zip"));

        Assert.Null(new UpdateSourcePolicy("").ValidateAssetUri(new Uri(
            "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/packages/BetterGI/BetterGI-0.1.0.zip")));
    }

    [Fact]
    public void IsCanonicalPluginId_EnforcesLowerKebabCase()
    {
        Assert.True(PluginRepositoryCatalog.IsCanonicalPluginId("bettergi"));
        Assert.True(PluginRepositoryCatalog.IsCanonicalPluginId("game-checkin"));
        Assert.True(PluginRepositoryCatalog.IsCanonicalPluginId("a1-b2"));
        Assert.False(PluginRepositoryCatalog.IsCanonicalPluginId("BetterGI"));
        Assert.False(PluginRepositoryCatalog.IsCanonicalPluginId("game_checkin"));
        Assert.False(PluginRepositoryCatalog.IsCanonicalPluginId("-game"));
        Assert.False(PluginRepositoryCatalog.IsCanonicalPluginId("game--checkin"));
    }

    [Fact]
    public void CompareVersions_UsesNumericSemverOrdering()
    {
        Assert.True(PluginRepositoryCatalog.CompareVersions("0.10.9", "0.10.8") > 0);
        Assert.True(PluginRepositoryCatalog.CompareVersions("1.2.0", "1.10.0") < 0);
        Assert.True(PluginRepositoryCatalog.CompareVersions("1.2.3-rc.1", "1.2.3-beta.9") > 0);
        Assert.True(PluginRepositoryCatalog.CompareVersions("1.2.3", "1.2.3-rc.9") > 0);
        Assert.Equal(0, PluginRepositoryCatalog.CompareVersions("0.1.0", "0.1.0"));
    }

    [Fact]
    public void UpdateEligibility_RequiresStoreOwnershipAndCurrentInstallableUpdate()
    {
        Assert.False(PluginRepositoryService.IsUpdateEligible(
            CreateStoreItem(installed: true, updateAvailable: true, compatible: true, managedByStore: false)));

        Assert.True(PluginRepositoryService.IsUpdateEligible(
            CreateStoreItem(installed: true, updateAvailable: true, compatible: true, managedByStore: true)));
        Assert.False(PluginRepositoryService.IsUpdateEligible(
            CreateStoreItem(installed: false, updateAvailable: true, compatible: true, managedByStore: true)));
        Assert.False(PluginRepositoryService.IsUpdateEligible(
            CreateStoreItem(installed: true, updateAvailable: false, compatible: true, managedByStore: true)));
        Assert.False(PluginRepositoryService.IsUpdateEligible(
            CreateStoreItem(installed: true, updateAvailable: true, compatible: true, managedByStore: true, pendingAction: "update")));

        Assert.True(PluginStoreProjector.IsCatalogArtifactMatch("FixtureArtifact", "FixtureArtifact"));
        Assert.False(PluginStoreProjector.IsCatalogArtifactMatch("FixtureArtifact", "Fixtureartifact"));
    }

    [Fact]
    public void CompatibilityEvaluator_DistinguishesHostApiAndInvalidVersionFailures()
    {
        PluginCatalogEntry managed = new(
            "fixture", "fixture", "", "", "0.1.0", "managed-code", "1.10", Array.Empty<string>(),
            UpdateService.CurrentVersion,
            "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/packages/fixture/fixture-0.1.0.zip",
            new string('a', 64),
            1);

        PluginCompatibilityResult api = PluginRepositoryCatalog.EvaluateCompatibility(managed, UpdateService.CurrentVersion);
        Assert.False(api.Compatible);
        Assert.Equal("plugin_api_incompatible", api.Code);
        Assert.Equal("incompatible", PluginRepositoryService.ResolveStoreStatus(api, installed: true, updateAvailable: true, pending: false));

        PluginCompatibilityResult host = PluginRepositoryCatalog.EvaluateCompatibility(
            managed with { ApiVersion = "1.0", MinHostVersion = CandidateHostVersion },
            UpdateService.CurrentVersion);
        Assert.Equal("host_version_too_low", host.Code);
        Assert.Equal("update-requires-host-upgrade", PluginRepositoryService.ResolveStoreStatus(host, installed: true, updateAvailable: true, pending: false));

        Assert.Equal("invalid_version", PluginRepositoryCatalog.EvaluateCompatibility(
            managed with { MinHostVersion = "not-a-version" },
            UpdateService.CurrentVersion).Code);

        // Plugin API 只比较 major/minor：1.x 可装，更高 minor 必须拒绝。
        PluginCatalogEntry currentApi = managed with { ApiVersion = "1.9", MinHostVersion = "0.11.0" };
        Assert.True(PluginRepositoryCatalog.IsCompatible(currentApi, "0.11.0", out _));
        Assert.False(PluginRepositoryCatalog.IsCompatible(currentApi with { ApiVersion = "1.10" }, "0.11.0", out string apiReason));
        Assert.Contains("Plugin API", apiReason);
        Assert.False(PluginRepositoryCatalog.TryParseApiVersion("1.2.0", out _, out _));
    }

    private static PluginStoreItem CreateStoreItem(
        bool installed,
        bool updateAvailable,
        bool compatible,
        bool managedByStore,
        string pendingAction = "",
        string name = "fixture")
    {
        return new PluginStoreItem(
            name,
            name,
            name,
            "",
            "",
            "0.2.0",
            "data-specialized",
            "",
            Array.Empty<string>(),
            "0.1.0",
            installed,
            installed ? "0.1.0" : "",
            updateAvailable,
            compatible,
            "",
            managedByStore,
            pendingAction,
            "",
            updateAvailable ? "update-available" : "installed",
            installed ? name : "",
            Array.Empty<PluginChangelogEntry>());
    }

    private static JsonObject CreateCatalog()
    {
        var entry = new JsonObject
        {
            ["name"] = "bettergi",
            ["displayName"] = "BetterGI",
            ["gameName"] = "原神",
            ["description"] = "测试插件",
            ["version"] = "0.1.0",
            ["kind"] = "data-specialized",
            ["apiVersion"] = "",
            ["capabilities"] = new JsonArray(),
            ["minHostVersion"] = "0.10.8",
            ["packageUrl"] = "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/packages/BetterGI/BetterGI-0.1.0.zip",
            ["sha256"] = new string('a', 64),
            ["sizeBytes"] = 128,
            ["artifactName"] = "BetterGI",
            ["changelog"] = new JsonArray(
                new JsonObject
                {
                    ["version"] = "0.1.0",
                    ["date"] = "2026-08-28",
                    ["items"] = new JsonArray("加入插件仓库支持。"),
                }),
        };
        var plugins = new JsonArray();
        plugins.Add(entry);
        return new JsonObject
        {
            ["schemaVersion"] = 2,
            ["repository"] = PluginRepositoryCatalog.Repository,
            ["generatedAt"] = "2026-08-27T00:00:00Z",
            ["plugins"] = plugins,
        };
    }
}

/// <summary>插件运行时门面：加载幂等与受控投影缓存。</summary>
public sealed class PluginManagerTests : IClassFixture<HostTestScope>
{
    private readonly HostCompositionRoot _context;

    public PluginManagerTests(HostTestScope host)
    {
        _context = host.Composition;
    }

    [Fact]
    public void LoadAll_DoesNotExposeRemovedBuiltInPlugins()
    {
        PluginManager manager = _context.Plugins;
        manager.LoadAll();
        string[] firstNames = manager.PluginSummaries.Select(plugin => plugin.Name).OrderBy(name => name).ToArray();

        manager.LoadAll();
        string[] secondNames = manager.PluginSummaries.Select(plugin => plugin.Name).OrderBy(name => name).ToArray();

        Assert.Equal(firstNames, secondNames);
        Assert.DoesNotContain("notify", firstNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("emulator-adapter", firstNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManagementProjection_IsCachedUntilInvalidated()
    {
        PluginManager manager = _context.Plugins;
        manager.LoadAll();

        IReadOnlyList<PluginManagementView> first = manager.PluginManagementViews;
        IReadOnlyList<PluginManagementView> second = manager.PluginManagementViews;

        Assert.Same(first, second);
        long revision = manager.PluginManagementRevision;
        manager.InvalidateManagementSnapshot();

        Assert.True(manager.PluginManagementRevision > revision);
        IReadOnlyList<PluginManagementView> refreshed = manager.PluginManagementViews;
        Assert.Equal(first, refreshed);
        if (first.Count > 0)
        {
            Assert.NotSame(first, refreshed);
        }
    }
}
