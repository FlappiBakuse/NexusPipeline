using System.Text.Json.Nodes;
using NexusPipeline.Persistence;
using NexusPipeline.Plugins;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class PluginNameMigrationTests
{
    [Theory]
    [InlineData("maastellasora", "maas")]
    [InlineData("MAASTELLASORA", "maas")]
    [InlineData(" maastellasora ", "maas")]
    [InlineData("maas", "maas")]
    [InlineData("bettergi", "bettergi")]
    public void Canonicalize_UsesCurrentPluginMachineName(string input, string expected)
    {
        Assert.Equal(expected, PluginNameMigration.Canonicalize(input));
    }

    [Fact]
    public async Task LegacyScopedStore_WritesIntoCanonicalNamespace()
    {
        string scope = $"migration-test-{Guid.NewGuid():N}";
        string canonicalPath = Path.Combine(AppPaths.ConfigDir, "plugins", "maas", "scopes", scope + ".json");
        string legacyPath = Path.Combine(AppPaths.ConfigDir, "plugins", "maastellasora", "scopes", scope + ".json");

        try
        {
            var store = new PluginScopedDataStore("maastellasora");
            await store.WriteJsonAsync(scope, new JsonObject { ["value"] = "preserved" });

            Assert.True(File.Exists(canonicalPath));
            Assert.False(File.Exists(legacyPath));
            JsonObject? loaded = await store.ReadJsonAsync(scope);
            Assert.Equal("preserved", loaded?["value"]?.GetValue<string>());
        }
        finally
        {
            if (File.Exists(canonicalPath))
            {
                File.Delete(canonicalPath);
            }
            if (File.Exists(legacyPath))
            {
                File.Delete(legacyPath);
            }
        }
    }
}
