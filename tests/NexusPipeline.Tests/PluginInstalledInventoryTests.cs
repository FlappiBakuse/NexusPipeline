using System.Text.Json.Nodes;
using NexusPipeline.Plugins;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class PluginInstalledInventoryTests
{
    [Fact]
    public void ReadSummaries_UsesValidManifestsAndRejectsAmbiguousNamesAndArtifacts()
    {
        string root = Path.Combine(Path.GetTempPath(), "np-plugin-inventory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            WriteManagedPlugin(root, "DisabledPlugin", "fixture-disabled", "1.0.0");
            WriteManagedPlugin(root, "DuplicateOne", "fixture-duplicate", "1.0.0");
            WriteManagedPlugin(root, "DuplicateTwo", "fixture-duplicate", "1.1.0");
            WriteManagedPlugin(root, "CaseMismatch", "fixture-wrong-directory", "1.0.0", artifactName: "Casemismatch");
            Directory.CreateDirectory(Path.Combine(root, "InvalidManifest"));
            File.WriteAllText(Path.Combine(root, "InvalidManifest", "plugin.json"), "{");

            IReadOnlyList<PluginSummary> summaries = PluginInstalledInventory.ReadSummaries(root);

            PluginSummary installed = Assert.Single(summaries);
            Assert.Equal("fixture-disabled", installed.Name);
            Assert.Equal("1.0.0", installed.Version);
            Assert.Equal("DisabledPlugin", installed.ArtifactName);
        }
        finally
        {
            PluginInstalledInventory.Invalidate(root);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void WriteManagedPlugin(
        string root,
        string directoryName,
        string name,
        string version,
        string? artifactName = null)
    {
        string directory = Path.Combine(root, directoryName);
        Directory.CreateDirectory(directory);
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["name"] = name,
            ["artifactName"] = artifactName ?? directoryName,
            ["displayName"] = directoryName,
            ["version"] = version,
            ["kind"] = "managed-code",
            ["apiVersion"] = "1.0",
            ["entryAssembly"] = "not-loaded.dll",
            ["entryType"] = "Fixture.Entry",
        };
        File.WriteAllText(Path.Combine(directory, "plugin.json"), manifest.ToJsonString());
    }
}
