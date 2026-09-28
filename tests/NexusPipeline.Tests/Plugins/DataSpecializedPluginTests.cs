using System.Text.Json;
using Xunit;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Plugins.DataSpecialized;
using NexusPipeline.Modules.Plugins.Managed;

namespace NexusPipeline.Tests.Plugins;

public sealed class DataSpecializedPluginTests
{
    [Fact]
    public void DataPluginDeclaresEmulatorCapabilityAndResolvesCurrentProfile()
    {
        string root = MakeTempDir();
        try
        {
            string pluginDir = Path.Combine(root, "current-plugin");
            string scriptRoot = Path.Combine(root, "script-root");
            Directory.CreateDirectory(Path.Combine(pluginDir, "data"));
            Directory.CreateDirectory(scriptRoot);
            string mainExe = Path.Combine(scriptRoot, "tool.exe");
            File.WriteAllText(mainExe, "placeholder");
            File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                name = "current",
                artifactName = "Current",
                displayName = "Current",
                version = "1.0.0",
                kind = "data-specialized",
                resolve = "data/resolve.json",
                judgeScript = "data/judge.js",
                capabilities = new[] { PluginCapabilityKeys.Emulator },
            }));
            File.WriteAllText(Path.Combine(pluginDir, "data", "resolve.json"), JsonSerializer.Serialize(new
            {
                outputEncoding = "windows-936",
                require = new[] { new { var = "tool", file = "tool.exe" } },
                paths = new { mainExe = "{tool}", args = "", configPath = "config.json", logPath = "log.txt" },
            }));
            File.WriteAllText(Path.Combine(pluginDir, "data", "judge.js"), "// judge");

            DataSpecializedPlugin plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));
            ScriptProfile profile = Assert.IsType<ScriptProfile>(plugin.Resolve(scriptRoot, null));

            Assert.Contains(PluginCapabilityKeys.Emulator, plugin.CapabilityKeys);
            Assert.Equal(mainExe, profile.MainExe);
            Assert.Equal("windows-936", profile.OutputEncoding);
            Assert.Equal("javascript", profile.JudgeScriptLanguage);
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public void DataPluginRejectsUnknownCapabilityAndFrontendField()
    {
        string root = MakeTempDir();
        try
        {
            string pluginDir = Path.Combine(root, "invalid-plugin");
            Directory.CreateDirectory(Path.Combine(pluginDir, "data"));
            File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                name = "invalid",
                artifactName = "Invalid",
                displayName = "Invalid",
                version = "1.0.0",
                kind = "data-specialized",
                resolve = "data/resolve.json",
                judgeScript = "data/judge.js",
                capabilities = new[] { "frontend-module" },
                frontend = (object?)null,
            }));
            File.WriteAllText(Path.Combine(pluginDir, "data", "resolve.json"), "{}");
            File.WriteAllText(Path.Combine(pluginDir, "data", "judge.js"), "// judge");

            Assert.False(PluginManifest.TryLoad(pluginDir, out _, out string? error));
            Assert.Contains("frontend", error);
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public void DataPluginRejectsBrowserDirectoryAndManagedPayload()
    {
        string root = MakeTempDir();
        try
        {
            string pluginDir = Path.Combine(root, "invalid-plugin");
            Directory.CreateDirectory(Path.Combine(pluginDir, "data"));
            Directory.CreateDirectory(Path.Combine(pluginDir, "web"));
            File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                name = "invalid",
                artifactName = "Invalid",
                displayName = "Invalid",
                version = "1.0.0",
                kind = "data-specialized",
                resolve = "data/resolve.json",
                judgeScript = "data/judge.js",
            }));
            File.WriteAllText(Path.Combine(pluginDir, "data", "resolve.json"), "{}");
            File.WriteAllText(Path.Combine(pluginDir, "data", "judge.js"), "// judge");
            File.WriteAllText(Path.Combine(pluginDir, "web", "main.js"), "export default {};");

            Assert.False(PluginManifest.TryLoad(pluginDir, out _, out string? error));
            Assert.Contains("浏览器目录", error);
        }
        finally
        {
            DeleteExact(root);
        }
    }

    private static string MakeTempDir()
    {
        string root = Path.Combine(Path.GetTempPath(), "np-data-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteExact(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
