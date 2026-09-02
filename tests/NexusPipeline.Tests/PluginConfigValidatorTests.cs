using System.Text.Json.Nodes;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Plugins;
using NexusPipeline.Plugins.Managed;
using NexusPipeline.Services.Notification;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class PluginConfigValidatorTests
{
    private static string MakeTempDir()
    {
        string root = Path.Combine(Path.GetTempPath(), "np-plugin-validator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void DataManifestLoadsOptionalJavaScriptValidator()
    {
        string root = MakeTempDir();
        try
        {
            WriteDataManifest(root, "data/config-validator.js");
            File.WriteAllText(Path.Combine(root, "data", "config-validator.js"), "nexus.toast('ok');");

            Assert.True(PluginManifest.TryLoad(root, out PluginManifest? manifest, out string? error), error);
            Assert.Equal("data/config-validator.js", manifest!.ConfigValidatorPath);
            Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(root));
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Theory]
    [InlineData("../config-validator.js")]
    [InlineData("data/../config-validator.js")]
    [InlineData("C:/outside/config-validator.js")]
    [InlineData("data/config-validator.py")]
    [InlineData("data/")]
    public void DataManifestRejectsUnsafeValidatorPath(string validatorPath)
    {
        string root = MakeTempDir();
        try
        {
            WriteDataManifest(root, validatorPath);
            File.WriteAllText(Path.Combine(root, "data", "config-validator.js"), "");

            Assert.False(PluginManifest.TryLoad(root, out _, out string? error));
            Assert.Contains("configValidator", error);
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public void DataManifestRejectsMissingValidatorFile()
    {
        string root = MakeTempDir();
        try
        {
            WriteDataManifest(root, "data/config-validator.js");
            Assert.False(PluginManifest.TryLoad(root, out _, out string? error));
            Assert.Contains("configValidator", error);
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public void ManagedManifestCannotOptIntoDataValidator()
    {
        string root = MakeTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "data"));
            File.WriteAllText(Path.Combine(root, "data", "config-validator.js"), "");
            var manifest = new JsonObject
            {
                ["schemaVersion"] = 2,
                ["name"] = "fixture-managed-validator",
                ["artifactName"] = "FixtureManagedValidator",
                ["displayName"] = "Fixture",
                ["version"] = "1.0.0",
                ["kind"] = "managed-code",
                ["apiVersion"] = "1.0",
                ["entryAssembly"] = "fixture.dll",
                ["entryType"] = "Fixture.Entry",
                ["configValidator"] = "data/config-validator.js",
            };
            File.WriteAllText(Path.Combine(root, "plugin.json"), manifest.ToJsonString());

            Assert.False(PluginManifest.TryLoad(root, out _, out string? error));
            Assert.Contains("data-specialized", error);
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public void PluginManagerReturnsValidatorOnlyForEnabledDataPlugin()
    {
        string root = MakeTempDir();
        string name = "fixture-validator-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            WriteDataManifest(root, "data/config-validator.js", name);
            File.WriteAllText(Path.Combine(root, "data", "config-validator.js"), "nexus.toast('fixture');");
            DataSpecializedPlugin plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(root));
            var settings = new AppSettings();
            var manager = new PluginManager(
                () => settings,
                () => new NotificationDispatcher(new LocalSettingsProvider()),
                discoverData: () => [plugin]);
            try
            {
                manager.LoadAll();
                Assert.True(manager.TryGetConfigValidator(name, out ConfigValidatorDescriptor? descriptor));
                Assert.Equal(name, descriptor!.PluginName);
                Assert.Equal("nexus.toast('fixture');", descriptor.Script);

                settings.PluginPreferences = new Dictionary<string, PluginPreference>(StringComparer.OrdinalIgnoreCase)
                {
                    [name] = new PluginPreference { Enabled = false },
                };
                manager.LoadAll();
                Assert.False(manager.TryGetConfigValidator(name, out _));
            }
            finally
            {
                manager.ShutdownAll();
            }
        }
        finally
        {
            DeleteExact(root);
        }
    }

    private static void WriteDataManifest(string root, string validatorPath, string name = "fixture-validator")
    {
        Directory.CreateDirectory(Path.Combine(root, "data"));
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["name"] = name,
            ["artifactName"] = "FixtureValidator",
            ["displayName"] = "Fixture Validator",
            ["version"] = "1.0.0",
            ["kind"] = "data-specialized",
            ["resolve"] = "data/resolve.json",
            ["judgeScript"] = "data/judge.js",
            ["configValidator"] = validatorPath,
        };
        File.WriteAllText(Path.Combine(root, "plugin.json"), manifest.ToJsonString());
        File.WriteAllText(Path.Combine(root, "data", "resolve.json"), "{\"paths\":{\"mainExe\":\"tool.exe\",\"configPath\":\"config.json\",\"logPath\":\"log.txt\"}}");
        File.WriteAllText(Path.Combine(root, "data", "judge.js"), "");
    }

    private static void DeleteExact(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class LocalSettingsProvider : ISettingsProvider
    {
        public AppSettings Current { get; } = new();
    }
}
