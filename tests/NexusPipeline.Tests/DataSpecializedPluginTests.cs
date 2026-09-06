using System.Text.Json;
using NexusPipeline.Extensibility;
using NexusPipeline.Models;
using NexusPipeline.Plugins;
using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

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
                capabilities = new[] { "probe", PluginCapabilityKeys.Emulator },
            }));
            File.WriteAllText(Path.Combine(pluginDir, "data", "resolve.json"), JsonSerializer.Serialize(new
            {
                require = new[] { new { var = "tool", file = "tool.exe" } },
                paths = new { mainExe = "{tool}", args = "", configPath = "config.json", logPath = "log.txt" },
            }));
            File.WriteAllText(Path.Combine(pluginDir, "data", "judge.js"), "// judge");

            DataSpecializedPlugin plugin = Assert.IsType<DataSpecializedPlugin>(DataSpecializedPlugin.Load(pluginDir));
            ScriptProfile profile = Assert.IsType<ScriptProfile>(plugin.Resolve(scriptRoot, null));

            Assert.Contains("probe", plugin.CapabilityKeys);
            Assert.Contains(PluginCapabilityKeys.Emulator, plugin.CapabilityKeys);
            Assert.Equal(mainExe, profile.MainExe);
            Assert.Equal("javascript", profile.JudgeScriptLanguage);
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
