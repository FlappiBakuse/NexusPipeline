using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Plugins;
using NexusPipeline.Services.Networking;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class PluginRepositoryCatalogTests
{
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
        Assert.Equal(0, PluginRepositoryCatalog.CompareVersions("0.1.0", "0.1.0"));
    }

    [Fact]
    public void PluginApiCompatibility_UsesMajorAndMinorVersion()
    {
        Assert.True(PluginRepositoryCatalog.TryParseApiVersion("1.0", out int major, out int minor));
        Assert.Equal(1, major);
        Assert.Equal(0, minor);
        Assert.True(PluginRepositoryCatalog.TryParseApiVersion("1.1", out _, out _));
        Assert.False(PluginRepositoryCatalog.TryParseApiVersion("1", out _, out _));
        Assert.False(PluginRepositoryCatalog.TryParseApiVersion("1.2.0", out _, out _));

        PluginCatalogEntry compatible = new(
            "fixture", "fixture", "", "", "0.1.0", "managed-code", "1.0", Array.Empty<string>(),
            "0.11.0", "https://github.com/FlappiBakuse/NexusPipeline-Plugins/releases/download/v0.1.0/fixture-0.1.0.zip", new string('a', 64), 1);
        Assert.True(PluginRepositoryCatalog.IsCompatible(compatible, "0.11.0", out _));
        PluginCatalogEntry newerMinor = compatible with { ApiVersion = "1.2" };
        Assert.False(PluginRepositoryCatalog.IsCompatible(newerMinor, "0.11.0", out string reason));
        Assert.Contains("Plugin API", reason);
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
            ["packageUrl"] = "https://github.com/FlappiBakuse/NexusPipeline-Plugins/releases/download/v0.1.0/bettergi-0.1.0.zip",
            ["sha256"] = new string('a', 64),
            ["sizeBytes"] = 128,
        };
        var plugins = new JsonArray();
        plugins.Add(entry);
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["repository"] = PluginRepositoryCatalog.Repository,
            ["generatedAt"] = "2026-08-27T00:00:00Z",
            ["plugins"] = plugins,
        };
    }
}

public sealed class ProxyConfigurationTests
{
    [Fact]
    public void ProxyModes_MapToExpectedHttpHandler()
    {
        using (HttpClientHandler none = ProxyConfiguration.FromSettings(new AppSettings
        {
            ProxyMode = "none",
        }).CreateHandler(OutboundHttpTarget.External, allowAutoRedirect: false))
        {
            Assert.False(none.UseProxy);
        }

        using (HttpClientHandler system = ProxyConfiguration.FromSettings(new AppSettings
        {
            ProxyMode = "system",
        }).CreateHandler(OutboundHttpTarget.External, allowAutoRedirect: false))
        {
            Assert.True(system.UseProxy);
            Assert.Null(system.Proxy);
        }

        using (HttpClientHandler custom = ProxyConfiguration.FromSettings(new AppSettings
        {
            ProxyMode = "http",
            ProxyUrl = "http://127.0.0.1:7890",
            ProxyUsername = "user",
            ProxyPassword = "password",
        }).CreateHandler(OutboundHttpTarget.External, allowAutoRedirect: false))
        {
            Assert.True(custom.UseProxy);
            WebProxy proxy = Assert.IsType<WebProxy>(custom.Proxy);
            Assert.Equal(new Uri("http://127.0.0.1:7890"), proxy.Address);
            Assert.NotNull(proxy.Credentials);
        }

        using (HttpClientHandler loopback = ProxyConfiguration.FromSettings(new AppSettings
        {
            ProxyMode = "http",
            ProxyUrl = "http://127.0.0.1:7890",
        }).CreateHandler(OutboundHttpTarget.Loopback, allowAutoRedirect: false))
        {
            Assert.False(loopback.UseProxy);
        }
    }

    [Fact]
    public void CustomProxy_RequiresHttpOrHttpsAddress()
    {
        Assert.Throws<InvalidDataException>(() => ProxyConfiguration.FromSettings(new AppSettings
        {
            ProxyMode = "http",
        }));
        Assert.Throws<InvalidDataException>(() => ProxyConfiguration.FromSettings(new AppSettings
        {
            ProxyMode = "http",
            ProxyUrl = "socks5://127.0.0.1:7890",
        }));
    }
}

public sealed class PluginInstallRecoveryTests
{
    [Fact]
    public void ApplyPending_InstallsAndUninstallsPluginTransactionally()
    {
        string root = NewTempDir();
        try
        {
            string plugins = Path.Combine(root, "plugins");
            string pending = Path.Combine(root, "state", "pending.json");
            string ownership = Path.Combine(root, "state", "ownership.json");
            string staging = Path.Combine(root, "state", "staging");
            string backup = Path.Combine(root, "state", "backup");
            string staged = Path.Combine(staging, "bettergi.1");
            Directory.CreateDirectory(staged);
            File.WriteAllText(Path.Combine(staged, "plugin.json"), "{}");

            PluginInstallRecovery.AddPending(new PluginPendingOperation
            {
                Action = "install",
                Name = "bettergi",
                Version = "0.1.0",
                Kind = "data-specialized",
                StagedPath = staged,
                Phase = "pending",
            }, pending);

            Assert.True(PluginInstallRecovery.ApplyPending(plugins, pending, ownership, staging, backup));
            Assert.True(File.Exists(Path.Combine(plugins, "bettergi", "plugin.json")));
            Assert.Empty(PluginInstallRecovery.ReadPending(pending));
            Assert.False(Directory.Exists(staging));
            Assert.False(Directory.Exists(backup));
            PluginOwnership installed = Assert.Single(PluginInstallRecovery.ReadOwnership(ownership).Values);
            Assert.Equal("bettergi", installed.Name);
            Assert.Equal("0.1.0", installed.Version);

            PluginInstallRecovery.AddPending(new PluginPendingOperation
            {
                Action = "uninstall",
                Name = "bettergi",
                Version = "0.1.0",
                Kind = "data-specialized",
                StagedPath = Path.Combine(staging, "uninstall.bettergi"),
                Phase = "pending",
            }, pending);

            Assert.True(PluginInstallRecovery.ApplyPending(plugins, pending, ownership, staging, backup));
            Assert.False(Directory.Exists(Path.Combine(plugins, "bettergi")));
            Assert.Empty(PluginInstallRecovery.ReadOwnership(ownership));
            Assert.Empty(PluginInstallRecovery.ReadPending(pending));
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void ApplyPending_CompletesSwapWhenJournalWriteWasInterrupted()
    {
        string root = NewTempDir();
        try
        {
            string plugins = Path.Combine(root, "plugins");
            string pending = Path.Combine(root, "state", "pending.json");
            string ownership = Path.Combine(root, "state", "ownership.json");
            string staging = Path.Combine(root, "state", "staging");
            string backup = Path.Combine(root, "state", "backup");
            string local = Path.Combine(plugins, "bettergi");
            string backupPath = Path.Combine(backup, "bettergi.previous");
            Directory.CreateDirectory(local);
            Directory.CreateDirectory(backupPath);
            File.WriteAllText(Path.Combine(local, "new.txt"), "new");
            File.WriteAllText(Path.Combine(backupPath, "old.txt"), "old");

            PluginInstallRecovery.AddPending(new PluginPendingOperation
            {
                Action = "update",
                Name = "bettergi",
                Version = "0.2.0",
                Kind = "data-specialized",
                StagedPath = Path.Combine(staging, "missing-after-swap"),
                BackupPath = backupPath,
                Phase = "backed-up",
            }, pending);

            Assert.True(PluginInstallRecovery.ApplyPending(plugins, pending, ownership, staging, backup));
            Assert.True(File.Exists(Path.Combine(local, "new.txt")));
            Assert.False(File.Exists(Path.Combine(local, "old.txt")));
            Assert.Empty(PluginInstallRecovery.ReadPending(pending));
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    [Fact]
    public void ApplyPending_FailedStageKeepsJournalForRetry()
    {
        string root = NewTempDir();
        try
        {
            string pending = Path.Combine(root, "state", "pending.json");
            string plugins = Path.Combine(root, "plugins");
            string staging = Path.Combine(root, "state", "staging");
            string backup = Path.Combine(root, "state", "backup");
            string ownership = Path.Combine(root, "state", "ownership.json");
            Directory.CreateDirectory(Path.Combine(plugins, "bettergi"));
            File.WriteAllText(Path.Combine(plugins, "bettergi", "old.txt"), "old");

            PluginInstallRecovery.AddPending(new PluginPendingOperation
            {
                Action = "update",
                Name = "bettergi",
                Version = "0.2.0",
                Kind = "data-specialized",
                StagedPath = Path.Combine(staging, "missing"),
                Phase = "pending",
            }, pending);

            Assert.False(PluginInstallRecovery.ApplyPending(plugins, pending, ownership, staging, backup));
            Assert.Equal("old", File.ReadAllText(Path.Combine(plugins, "bettergi", "old.txt")));
            Assert.Single(PluginInstallRecovery.ReadPending(pending));
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    private static string NewTempDir()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-plugin-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempDir(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
