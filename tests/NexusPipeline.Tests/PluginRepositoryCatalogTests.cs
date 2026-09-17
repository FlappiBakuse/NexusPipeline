using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Plugins;
using NexusPipeline.Services.Networking;
using NexusPipeline.Services.Update;
using NexusPipeline.Utilities;
using Xunit;

namespace NexusPipeline.Tests;

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
        string json = CreateCatalog().ToJsonString();

        Assert.True(PluginRepositoryCatalog.TryParse(json, out PluginCatalog? catalog, out string? error), error);
        PluginCatalogEntry entry = Assert.Single(catalog!.Plugins);
        Assert.Equal("bettergi", entry.Name);
        Assert.Equal("data-specialized", entry.Kind);
        Assert.Equal("0.10.8", entry.MinHostVersion);
        Assert.True(PluginRepositoryCatalog.IsCompatible(entry, "0.10.8", out _));
        Assert.False(PluginRepositoryCatalog.IsCompatible(entry, "0.10.7", out string reason));
        Assert.Contains("需要宿主", reason);
    }

    [Fact]
    public void TryParse_Schema2_ParsesArtifactAndChangelog()
    {
        string json = CreateCatalog(2).ToJsonString();

        Assert.True(PluginRepositoryCatalog.TryParse(json, out PluginCatalog? catalog, out string? error), error);
        PluginCatalogEntry entry = Assert.Single(catalog!.Plugins);
        Assert.Equal(2, catalog.SchemaVersion);
        Assert.Equal("BetterGI", entry.ArtifactName);
        Assert.Equal("0.1.0", Assert.Single(entry.Changelog).Version);
    }

    [Fact]
    public void TryParse_ParsesPresentationMetadataAndDerivesUpdatedAt()
    {
        JsonObject root = CreateCatalog(2);
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
        PluginCatalogEntry parsed = Assert.Single(catalog!.Plugins);
        Assert.Equal("Nexus Team", Assert.Single(parsed.Authors).Name);
        Assert.Equal(new[] { "原神", "专项插件" }, parsed.Tags);
        Assert.Equal("https://github.com/FlappiBakuse/NexusPipeline-Plugins", parsed.Homepage);
        Assert.Equal("2026-08-20", parsed.CreatedAt);
        Assert.Equal("2026-08-28", parsed.UpdatedAt);
        Assert.True(parsed.HasReadme);
    }

    [Fact]
    public void TryParse_CreatedAtIsOptionalForOlderCatalogsAndValidatedWhenPresent()
    {
        JsonObject root = CreateCatalog(2);
        Assert.True(PluginRepositoryCatalog.TryParse(root.ToJsonString(), out PluginCatalog? legacyCatalog, out string? legacyError), legacyError);
        Assert.Equal("", Assert.Single(legacyCatalog!.Plugins).CreatedAt);

        root = CreateCatalog(2);
        ((JsonObject)((JsonArray)root["plugins"]!)[0]!) ["createdAt"] = "2026-08-29";
        Assert.False(PluginRepositoryCatalog.TryParse(root.ToJsonString(), out _, out string? futureError));
        Assert.Contains("createdAt", futureError);

        root = CreateCatalog(2);
        ((JsonObject)((JsonArray)root["plugins"]!)[0]!) ["createdAt"] = "2026-02-30";
        Assert.False(PluginRepositoryCatalog.TryParse(root.ToJsonString(), out _, out string? invalidError));
        Assert.Contains("createdAt", invalidError);
    }

    [Fact]
    public void LoadLocalPresentationMetadata_ParsesCreatedAt()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nxp-plugin-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["gameName"] = "测试游戏",
                ["authors"] = new JsonArray(new JsonObject { ["name"] = "Nexus Team" }),
                ["tags"] = new JsonArray("测试"),
                ["homepage"] = "https://github.com/FlappiBakuse/NexusPipeline-Plugins",
                ["createdAt"] = "2026-08-20",
                ["changelog"] = new JsonArray(new JsonObject
                {
                    ["version"] = "1.0.0",
                    ["date"] = "2026-08-28",
                    ["items"] = new JsonArray("测试版本"),
                }),
            };
            File.WriteAllText(Path.Combine(directory, "store.json"), store.ToJsonString());

            PluginPresentationMetadata metadata = PluginPresentationMetadataParser.LoadLocal(directory, "通用", "1.0.0");

            Assert.Equal("2026-08-20", metadata.CreatedAt);
            Assert.Equal("2026-08-28", metadata.UpdatedAt);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://example.com/readme")]
    public void TryParse_RejectsUnsafePresentationHomepage(string homepage)
    {
        JsonObject root = CreateCatalog(2);
        ((JsonObject)((JsonArray)root["plugins"]!)[0]!) ["homepage"] = homepage;

        Assert.False(PluginRepositoryCatalog.TryParse(root.ToJsonString(), out _, out string? error));
        Assert.Contains("homepage", error);
    }

    [Fact]
    public void TryParse_Schema2_RequiresValidArtifactNameAndCurrentChangelog()
    {
        JsonObject root = CreateCatalog(2);
        JsonObject entry = (JsonObject)((JsonArray)root["plugins"]!)[0]!;
        entry["artifactName"] = "bettergi";
        Assert.False(PluginRepositoryCatalog.TryParse(root.ToJsonString(), out _, out string? artifactError));
        Assert.Contains("artifactName", artifactError);

        root = CreateCatalog(2);
        entry = (JsonObject)((JsonArray)root["plugins"]!)[0]!;
        entry["version"] = "0.1.1";
        entry["packageUrl"] = "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/packages/BetterGI/BetterGI-0.1.1.zip";
        Assert.False(PluginRepositoryCatalog.TryParse(root.ToJsonString(), out _, out string? changelogError));
        Assert.Contains("changelog", changelogError);
    }

    [Theory]
    [InlineData("bettergi", true)]
    [InlineData("game-checkin", true)]
    [InlineData("a1-b2", true)]
    [InlineData("BetterGI", false)]
    [InlineData("game_checkin", false)]
    [InlineData("game.checkin", false)]
    [InlineData("-game", false)]
    [InlineData("game-", false)]
    [InlineData("game--checkin", false)]
    public void IsCanonicalPluginId_EnforcesLowerKebabCase(string value, bool expected)
    {
        Assert.Equal(expected, PluginRepositoryCatalog.IsCanonicalPluginId(value));
    }

    [Fact]
    public void TryParse_Schema2_RejectsNonCanonicalMachineId()
    {
        JsonObject root = CreateCatalog(2);
        ((JsonObject)((JsonArray)root["plugins"]!)[0]!) ["name"] = "BetterGI";

        Assert.False(PluginRepositoryCatalog.TryParse(root.ToJsonString(), out _, out string? error));
        Assert.Contains("name", error);
    }

    [Fact]
    public void TryParse_RejectsDuplicateNamesAndUntrustedPackageUrl()
    {
        JsonObject root = CreateCatalog();
        JsonArray plugins = (JsonArray)root["plugins"]!;
        plugins.Add(((JsonObject)plugins[0]!).DeepClone());

        Assert.False(PluginRepositoryCatalog.TryParse(root.ToJsonString(), out _, out string? duplicateError));
        Assert.Contains("重复", duplicateError);

        root = CreateCatalog();
        ((JsonObject)((JsonArray)root["plugins"]!)[0]!) ["packageUrl"] = "https://example.com/plugin.zip";
        Assert.False(PluginRepositoryCatalog.TryParse(root.ToJsonString(), out _, out string? urlError));
        Assert.Contains("官方插件仓库", urlError);
    }

    [Theory]
    [InlineData("https://github.com/FlappiBakuse/NexusPipeline-Plugins/releases/download/v0.1.0/bettergi-0.1.0.txt")]
    [InlineData("https://github.com/FlappiBakuse/NexusPipeline-Plugins/releases/download/v0.1.0/sub/bettergi-0.1.0.zip")]
    [InlineData("https://github.com/FlappiBakuse/NexusPipeline-Plugins/releases/download/v0.1.0/../bettergi-0.1.0.zip")]
    public void ValidatePackageUrl_RejectsInvalidReleaseAssetPath(string url)
    {
        Assert.NotNull(PluginRepositoryCatalog.ValidatePackageUrl(url));
    }

    [Fact]
    public void ValidatePackageUrl_AcceptsOfficialRawPackage()
    {
        Assert.Null(PluginRepositoryCatalog.ValidatePackageUrl(
            "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/packages/CustomWallpaper/CustomWallpaper-0.1.1.zip"));
    }

    [Theory]
    [InlineData("https://raw.githubusercontent.com/other-owner/NexusPipeline-Plugins/main/packages/CustomWallpaper/CustomWallpaper-0.1.1.zip")]
    [InlineData("https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/dev/packages/CustomWallpaper/CustomWallpaper-0.1.1.zip")]
    [InlineData("https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/packages/customwallpaper/CustomWallpaper-0.1.1.zip")]
    [InlineData("https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/packages/CustomWallpaper/customwallpaper-0.1.1.zip")]
    [InlineData("https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/packages/CustomWallpaper/CustomWallpaper-0.1.1.zip?x=1")]
    [InlineData("https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/packages/CustomWallpaper/../CustomWallpaper-0.1.1.zip")]
    public void ValidatePackageUrl_RejectsUntrustedRawPackage(string url)
    {
        Assert.NotNull(PluginRepositoryCatalog.ValidatePackageUrl(url));
    }

    [Fact]
    public void ValidatePackageUrl_RejectsVersionMismatchWhenIdentityIsProvided()
    {
        Assert.NotNull(PluginRepositoryCatalog.ValidatePackageUrl(
            "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/packages/CustomWallpaper/CustomWallpaper-0.1.0.zip",
            artifactName: "CustomWallpaper",
            version: "0.1.1"));
    }

    [Fact]
    public void UpdateSourcePolicy_AllowsOfficialRawGithubAssets()
    {
        var policy = new UpdateSourcePolicy("");

        Assert.Null(policy.ValidateAssetUri(new Uri(
            "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/packages/BetterGI/BetterGI-0.1.0.zip")));
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.-1")]
    [InlineData("1.2.x")]
    [InlineData("01.2.3")]
    public void TryParse_RejectsNonSemverPluginVersion(string version)
    {
        JsonObject root = CreateCatalog();
        ((JsonObject)((JsonArray)root["plugins"]!)[0]!) ["version"] = version;

        Assert.False(PluginRepositoryCatalog.TryParse(root.ToJsonString(), out _, out string? error));
        Assert.Contains("version", error);
    }

    [Fact]
    public void CompareVersions_UsesNumericSemverOrdering()
    {
        Assert.True(PluginRepositoryCatalog.CompareVersions("0.10.9", "0.10.8") > 0);
        Assert.True(PluginRepositoryCatalog.CompareVersions("1.2.0", "1.10.0") < 0);
        Assert.True(PluginRepositoryCatalog.CompareVersions("1.2.3-beta.2", "1.2.3-beta.1") > 0);
        Assert.True(PluginRepositoryCatalog.CompareVersions("1.2.3-rc.1", "1.2.3-beta.9") > 0);
        Assert.True(PluginRepositoryCatalog.CompareVersions("1.2.3", "1.2.3-rc.9") > 0);
        Assert.Equal(0, PluginRepositoryCatalog.CompareVersions("0.1.0", "0.1.0"));
    }

    [Fact]
    public void UpdateEligibility_DoesNotRequireStoreOwnership()
    {
        PluginStoreItem unmanaged = CreateStoreItem(
            installed: true,
            updateAvailable: true,
            compatible: true,
            managedByStore: false);

        Assert.True(PluginRepositoryService.IsUpdateEligible(unmanaged));
    }

    [Theory]
    [InlineData("FixtureArtifact", "FixtureArtifact", true)]
    [InlineData("FixtureArtifact", "Fixtureartifact", false)]
    [InlineData("ManualInstall", "OfficialArtifact", false)]
    public void CatalogUpdateRequiresExactArtifactIdentity(
        string installedArtifactName,
        string catalogArtifactName,
        bool expected)
    {
        Assert.Equal(
            expected,
            PluginStoreProjector.IsCatalogArtifactMatch(installedArtifactName, catalogArtifactName));
    }

    [Theory]
    [InlineData(false, true, true, "", false)]
    [InlineData(true, false, true, "", false)]
    [InlineData(true, true, false, "", false)]
    [InlineData(true, true, true, "update", false)]
    [InlineData(true, true, true, "", true)]
    public void UpdateEligibility_RequiresCurrentInstallableUpdate(
        bool installed,
        bool compatible,
        bool updateAvailable,
        string pendingAction,
        bool expected)
    {
        PluginStoreItem item = CreateStoreItem(
            installed,
            updateAvailable,
            compatible,
            managedByStore: true,
            pendingAction: pendingAction);

        Assert.Equal(expected, PluginRepositoryService.IsUpdateEligible(item));
    }

    [Fact]
    public async Task StageUpdatesAsync_PreservesEarlierPluginWhenLaterStageIsCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        PluginStoreItem first = CreateStoreItem(true, true, true, managedByStore: false, name: "first");
        PluginStoreItem second = CreateStoreItem(true, true, true, managedByStore: false, name: "second");

        PluginBatchUpdateResult result = await PluginRepositoryService.StageUpdatesAsync(
            new[] { first, second },
            (candidate, token) =>
            {
                if (candidate.Name == "second")
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                return Task.FromResult(new PluginPendingOperation
                {
                    Action = "update",
                    Name = candidate.Name,
                    ArtifactName = candidate.ArtifactName,
                    Version = candidate.Version,
                });
            },
            cancellation.Token);

        Assert.True(result.Canceled);
        Assert.Equal("first", Assert.Single(result.Updated).Name);
        Assert.Empty(result.Failed);
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

    [Fact]
    public void PluginApiCompatibility_UsesMajorAndMinorVersion()
    {
        Assert.Equal(1, PluginApiVersion.Major);
        Assert.Equal(8, PluginApiVersion.Minor);
        Assert.True(PluginRepositoryCatalog.TryParseApiVersion("1.0", out int major, out int minor));
        Assert.Equal(1, major);
        Assert.Equal(0, minor);
        Assert.True(PluginRepositoryCatalog.TryParseApiVersion("1.1", out _, out _));
        Assert.False(PluginRepositoryCatalog.TryParseApiVersion("1", out _, out _));
        Assert.False(PluginRepositoryCatalog.TryParseApiVersion("1.2.0", out _, out _));

        PluginCatalogEntry compatible = new(
            "fixture", "fixture", "", "", "0.1.0", "managed-code", "1.0", Array.Empty<string>(),
            "0.11.0", "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/packages/fixture/fixture-0.1.0.zip", new string('a', 64), 1);
        Assert.True(PluginRepositoryCatalog.IsCompatible(compatible, "0.11.0", out _));
        PluginCatalogEntry previousMinor = compatible with { ApiVersion = "1.6" };
        Assert.True(PluginRepositoryCatalog.IsCompatible(previousMinor, "0.11.0", out _));
        PluginCatalogEntry currentMinor = compatible with { ApiVersion = "1.8" };
        Assert.True(PluginRepositoryCatalog.IsCompatible(currentMinor, "0.11.0", out _));
        PluginCatalogEntry newerMinor = compatible with { ApiVersion = "1.9" };
        Assert.False(PluginRepositoryCatalog.IsCompatible(newerMinor, "0.11.0", out string reason));
        Assert.Contains("Plugin API", reason);

        PluginCatalogEntry currentApi = compatible with { ApiVersion = "1.5", MinHostVersion = CandidateHostVersion };
        Assert.True(PluginRepositoryCatalog.IsCompatible(currentApi, CandidateHostVersion, out _));
    }

    [Fact]
    public void CompatibilityEvaluator_DistinguishesHostApiAndInvalidVersionFailures()
    {
        PluginCatalogEntry managed = new(
            "fixture", "fixture", "", "", "0.1.0", "managed-code", "1.9", Array.Empty<string>(),
            UpdateService.CurrentVersion,
            "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/packages/fixture/fixture-0.1.0.zip",
            new string('a', 64),
            1);

        PluginCompatibilityResult api = PluginRepositoryCatalog.EvaluateCompatibility(
            managed,
            UpdateService.CurrentVersion);
        Assert.False(api.Compatible);
        Assert.Equal("plugin_api_incompatible", api.Code);
        Assert.Equal("incompatible", PluginRepositoryService.ResolveStoreStatus(api, installed: true, updateAvailable: true, pending: false));

        PluginCompatibilityResult host = PluginRepositoryCatalog.EvaluateCompatibility(
            managed with { ApiVersion = "1.0", MinHostVersion = CandidateHostVersion },
            UpdateService.CurrentVersion);
        Assert.False(host.Compatible);
        Assert.Equal("host_version_too_low", host.Code);
        Assert.Equal("incompatible", PluginRepositoryService.ResolveStoreStatus(host, installed: false, updateAvailable: false, pending: false));
        Assert.Equal("update-requires-host-upgrade", PluginRepositoryService.ResolveStoreStatus(host, installed: true, updateAvailable: true, pending: false));

        PluginCompatibilityResult invalid = PluginRepositoryCatalog.EvaluateCompatibility(
            managed with { MinHostVersion = "not-a-version" },
            UpdateService.CurrentVersion);
        Assert.False(invalid.Compatible);
        Assert.Equal("invalid_version", invalid.Code);
    }

    private static JsonObject CreateCatalog(int schemaVersion = 2)
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
        };
        entry["artifactName"] = "BetterGI";
        entry["changelog"] = new JsonArray(
            new JsonObject
            {
                ["version"] = "0.1.0",
                ["date"] = "2026-08-28",
                ["items"] = new JsonArray("加入插件仓库支持。"),
            });
        var plugins = new JsonArray();
        plugins.Add(entry);
        return new JsonObject
        {
            ["schemaVersion"] = schemaVersion,
            ["repository"] = PluginRepositoryCatalog.Repository,
            ["generatedAt"] = "2026-08-27T00:00:00Z",
            ["plugins"] = plugins,
        };
    }
}

