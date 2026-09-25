using System.Text.Json.Nodes;
using NexusPipeline.Modules.Plugins.Managed;
using Xunit;

namespace NexusPipeline.Tests.Plugins;

public sealed class PluginConfigValidatorTests
{
    [Theory]
    [InlineData("data/config-validator.js", "data-specialized")]
    [InlineData("../unsafe.js", "data-specialized")]
    [InlineData("data/config-validator.js", "managed-code")]
    public void RetiredValidatorIsRejectedWithActionableMessage(string path, string kind)
    {
        string root = Path.Combine(Path.GetTempPath(), "np-retired-validator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        try
        {
            var manifest = new JsonObject
            {
                ["schemaVersion"] = 2,
                ["name"] = "fixture-validator",
                ["artifactName"] = "FixtureValidator",
                ["version"] = "1.0.0",
                ["kind"] = kind,
                ["resolve"] = "data/resolve.json",
                ["judgeScript"] = "data/judge.js",
                ["apiVersion"] = "1.0",
                ["entryAssembly"] = "fixture.dll",
                ["entryType"] = "Fixture.Entry",
                ["configValidator"] = path,
            };
            File.WriteAllText(Path.Combine(root, "plugin.json"), manifest.ToJsonString());
            File.WriteAllText(Path.Combine(root, "data", "config-validator.js"), "throw new Error('must not execute');");
            Assert.False(PluginManifest.TryLoad(root, out _, out string? error));
            Assert.Contains("configValidator", error);
            Assert.Contains("升级", error);
            Assert.Contains("卸载", error);
            Assert.Contains("不会被修改", error);
            Assert.True(File.Exists(Path.Combine(root, "data", "config-validator.js")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
