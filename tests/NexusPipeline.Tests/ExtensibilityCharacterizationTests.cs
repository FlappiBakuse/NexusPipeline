using System.Text.Json;
using NexusPipeline.Extensibility;
using NexusPipeline.Models;
using NexusPipeline.Plugins;
using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>核心行为特征：重构不得改变结果分类、配置形态和数据化插件能力语义。</summary>
public class ExtensibilityCharacterizationTests
{
    private static string MakeTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "np-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void RunAttemptResult_PreservesFatalAndCancellationSemantics()
    {
        RunAttemptResult fatal = RunAttemptResult.Fatal("超时");
        RunAttemptResult cancelled = RunAttemptResult.Cancelled("已取消");
        RunAttemptResult failed = RunAttemptResult.Failed("普通失败");

        Assert.Equal("failed", fatal.Status);
        Assert.True(fatal.IsFatal);
        Assert.Equal("cancelled", cancelled.Status);
        Assert.True(cancelled.IsFatal);
        Assert.False(failed.IsFatal);
    }

    [Fact]
    public void ConfigSwapPrimitives_CopyAs_FileAndDirectoryKeepContents()
    {
        string root = MakeTempDir();
        string sourceFile = Path.Combine(root, "source.json");
        string fileTarget = Path.Combine(root, "nested", "target.json");
        File.WriteAllText(sourceFile, "{\"value\":1}");

        ConfigSwapPrimitives.CopyAs(sourceFile, fileTarget, PathKind.File);

        string sourceDir = Path.Combine(root, "source-dir");
        string dirTarget = Path.Combine(root, "dir-target");
        Directory.CreateDirectory(Path.Combine(sourceDir, "child"));
        File.WriteAllText(Path.Combine(sourceDir, "child", "state.txt"), "state");
        ConfigSwapPrimitives.CopyAs(sourceDir, dirTarget, PathKind.Dir);

        Assert.Equal("{\"value\":1}", File.ReadAllText(fileTarget));
        Assert.Equal("state", File.ReadAllText(Path.Combine(dirTarget, "child", "state.txt")));
    }

    [Fact]
    public void ConfigSwapPrimitives_MoveAs_RemovesSourceAfterCopy()
    {
        string root = MakeTempDir();
        string source = Path.Combine(root, "source");
        string target = Path.Combine(root, "target");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "state.txt"), "state");

        ConfigSwapPrimitives.MoveAs(source, target, PathKind.Dir);

        Assert.False(Directory.Exists(source));
        Assert.Equal("state", File.ReadAllText(Path.Combine(target, "state.txt")));
    }

    [Fact]
    public void DataPlugin_DeclaresEmulatorCapability()
    {
        string root = MakeTempDir();
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
        ScriptProfile profile = Assert.IsType<ScriptProfile>(plugin.Resolve(scriptRoot));

        Assert.Contains("probe", plugin.CapabilityKeys);
        Assert.Contains(PluginCapabilityKeys.Emulator, plugin.CapabilityKeys);
        Assert.Equal(mainExe, profile.MainExe);
        Assert.Equal("javascript", profile.JudgeScriptLanguage);
    }

    [Fact]
    public void LogMonitor_ReadsOnlyNewContentAfterInitialPosition()
    {
        string root = MakeTempDir();
        string path = Path.Combine(root, "run.log");
        File.WriteAllText(path, "old\n");
        using var monitor = new LogMonitor(path, readFromStart: false, initialPosition: new FileInfo(path).Length);

        File.AppendAllText(path, "new\n");

        Assert.Equal("new\n", monitor.ReadNew());
        Assert.Equal("", monitor.ReadNew());
    }
}
