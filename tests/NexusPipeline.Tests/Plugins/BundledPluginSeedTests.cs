using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Modules.Plugins.Repository;
using NexusPipeline.Modules.Plugins.Managed;
using Xunit;

namespace NexusPipeline.Tests.Plugins;

public sealed class BundledPluginSeedTests
{
    [Fact]
    public void OnlyExactFreshPayloadGetsOwnershipAndDeletionDoesNotReseed()
    {
        string root = Path.Combine(Path.GetTempPath(), "np-bundled-seed-" + Guid.NewGuid().ToString("N"));
        string plugins = Path.Combine(root, "plugins");
        string owner = Path.Combine(root, ".nxp", "state", "plugins", "ownership.json");
        string pending = Path.Combine(root, ".nxp", "state", "plugins", "pending.json");
        Directory.CreateDirectory(plugins);
        try
        {
            var records = new List<object>();
            foreach (string artifact in new[] { "EmulatorSupport", "LiveScreenshot" })
            {
                string dir = Path.Combine(plugins, artifact);
                Directory.CreateDirectory(dir);
                string name = artifact == "EmulatorSupport" ? "emulator-support" : "live-screenshot";
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = 2,
                    name,
                    artifactName = artifact,
                    version = "1.0.0",
                    minHostVersion = "0.16.5",
                    kind = "managed-code",
                    apiVersion = "1.7",
                    entryAssembly = "fixture.dll",
                    entryType = "Fixture.Entry",
                });
                File.WriteAllBytes(Path.Combine(dir, "plugin.json"), bytes);
                Assert.True(PluginManifest.TryLoad(dir, out _, out string? manifestError), manifestError);
                records.Add(new
                {
                    name,
                    artifactName = artifact,
                    version = "1.0.0",
                    minHostVersion = "0.16.5",
                    kind = "managed-code",
                    apiVersion = "1.7",
                    packageSha256 = new string('a', 64),
                    files = new[] { new { path = "plugin.json", sizeBytes = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() } },
                });
            }
            string manifest = JsonSerializer.Serialize(new { schemaVersion = 1, repository = "FlappiBakuse/NexusPipeline-Plugins", plugins = records });
            Assert.True(PluginInstallRecovery.SeedBundledForNewInstall(true, plugins, owner, pending, manifest));
            var installed = PluginInstallRecovery.ReadOwnership(owner);
            Assert.Equal(2, installed.Count);
            Assert.All(installed.Values, item => Assert.Equal("stable", item.Channel));
            File.Delete(owner);
            Directory.Delete(Path.Combine(plugins, "LiveScreenshot"), recursive: true);
            Assert.False(PluginInstallRecovery.SeedBundledForNewInstall(false, plugins, owner, pending, manifest));
            Assert.False(PluginInstallRecovery.SeedBundledForNewInstall(true, plugins, owner, pending, manifest));
            Assert.False(File.Exists(owner));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UnknownDirectoryOrChangedByteIsNeverAdopted()
    {
        string root = Path.Combine(Path.GetTempPath(), "np-bundled-seed-" + Guid.NewGuid().ToString("N"));
        string plugins = Path.Combine(root, "plugins");
        Directory.CreateDirectory(plugins);
        try
        {
            Directory.CreateDirectory(Path.Combine(plugins, "EmulatorSupport"));
            Directory.CreateDirectory(Path.Combine(plugins, "LiveScreenshot"));
            Directory.CreateDirectory(Path.Combine(plugins, "Unknown"));
            Assert.False(PluginInstallRecovery.SeedBundledForNewInstall(true, plugins,
                Path.Combine(root, "ownership.json"), Path.Combine(root, "pending.json")));
            Assert.False(File.Exists(Path.Combine(root, "ownership.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
