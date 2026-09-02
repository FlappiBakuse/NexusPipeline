using System.Text.Json;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Plugins;
using NexusPipeline.Services.Configuration;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class ConfigValidationScriptRunnerTests
{
    private static string MakeTempDir()
    {
        string root = Path.Combine(Path.GetTempPath(), "np-config-validator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static ScriptInstance MakeScript()
    {
        return new ScriptInstance
        {
            Id = "script-validator",
            Name = "配置校验测试",
            PluginType = "fixture-validator",
            RootPath = "D:/games/fixture",
            MainExe = "D:/games/fixture/game.exe",
            Args = "--profile default",
            ConfigPath = "D:/games/fixture/config.json",
            LogPath = "D:/games/fixture/run.log",
            LaunchGame = true,
            GameMode = "pc",
            GameExe = "D:/games/game.exe",
            GameArgs = "--windowed",
            GameWaitSeconds = 12,
            ForceCloseGame = true,
            MaxAttempts = 4,
            LogStallTimeoutMinutes = 6,
            TotalTimeoutMinutes = 90,
            AutoUpdateConfig = false,
        };
    }

    private static ConfigValidatorDescriptor Descriptor(string code, string root)
    {
        return new ConfigValidatorDescriptor(
            "fixture-validator",
            root,
            Path.Combine(root, "config-validator.js"),
            code);
    }

    [Fact]
    public void BuildInputUsesStableLowerCamelDtoAndIncludesSnapshotMetadata()
    {
        ScriptInstance script = MakeScript();
        var user = new ResolvedScriptUser(
            "user-1",
            "用户甲",
            new UserScriptBinding { ScriptInstanceId = script.Id });
        string json = ConfigValidationScriptRunner.BuildInput(
            script,
            user,
            [new ConfigValidationFile("config.json", 12), new ConfigValidationFile("profiles/user.json", 34)]);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal("script-validator", root.GetProperty("script").GetProperty("id").GetString());
        Assert.Equal("fixture-validator", root.GetProperty("script").GetProperty("pluginType").GetString());
        Assert.False(root.GetProperty("script").GetProperty("autoUpdateConfig").GetBoolean());
        Assert.Equal("user-1", root.GetProperty("user").GetProperty("userId").GetString());
        Assert.Equal("用户甲", root.GetProperty("user").GetProperty("userName").GetString());
        Assert.Equal("profiles/user.json", root.GetProperty("snapshot").GetProperty("files")[1].GetProperty("path").GetString());
        Assert.Equal(34, root.GetProperty("snapshot").GetProperty("files")[1].GetProperty("size").GetInt64());
    }

    [Fact]
    public async Task JavaScriptReadsListsWritesUtf8AndQueuesFeedback()
    {
        string root = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, "config.json"), "旧配置");
            string code = """
                if (!nexus.exists('config.json')) throw new Error('missing');
                if (nexus.readFile('config.json') !== '旧配置') throw new Error('read');
                if (!nexus.listFiles().includes('config.json')) throw new Error('list');
                nexus.writeFile('profiles/user.json', '新配置✓');
                nexus.toast('已自动修复配置', 'success');
                nexus.notify('配置检查', '发现旧字段并已自动迁移。', 'warning');
                """;

            ConfigValidationResult result = await ConfigValidationScriptRunner.ExecuteAsync(
                Descriptor(code, root),
                MakeScript(),
                null,
                root);

            Assert.True(result.Ran);
            Assert.Equal("", result.Error);
            Assert.Contains("profiles/user.json", result.ChangedFiles);
            Assert.Equal("新配置✓", File.ReadAllText(Path.Combine(root, "profiles", "user.json")));
            Assert.Equal("已自动修复配置", Assert.Single(result.Toasts).Message);
            Assert.Equal("warning", Assert.Single(result.Notifications).Kind);
            Assert.Empty(Directory.GetFiles(root, "*.nexus-validator-*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public async Task InvalidPathsAndOversizedFilesAreRejectedWithoutEscapingStore()
    {
        string root = MakeTempDir();
        string outside = Path.Combine(Path.GetDirectoryName(root)!, "np-validator-escape-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(Path.Combine(root, "config.json"), "配置");
            string absolute = JsonSerializer.Serialize(outside);
            string code = $$"""
                if (nexus.readFile('../outside.txt') !== null) throw new Error('escape-read');
                if (nexus.exists('../outside.txt')) throw new Error('escape-exists');
                if (nexus.writeFile('../outside.txt', 'blocked')) throw new Error('escape-write');
                if (nexus.writeFile({{absolute}}, 'blocked')) throw new Error('absolute-write');
                if (nexus.readFile('missing.json') !== null) throw new Error('missing-read');
                """;

            ConfigValidationResult result = await ConfigValidationScriptRunner.ExecuteAsync(
                Descriptor(code, root),
                MakeScript(),
                null,
                root);

            Assert.True(result.Ran);
            Assert.Equal("", result.Error);
            Assert.False(File.Exists(outside));
            Assert.Empty(result.ChangedFiles);
        }
        finally
        {
            DeleteExact(outside);
            DeleteExact(root);
        }
    }

    [Fact]
    public async Task ValidatorErrorDoesNotRollbackEarlierAtomicWrites()
    {
        string root = MakeTempDir();
        try
        {
            string code = "nexus.writeFile('first.json', '保留'); throw new Error('after-write');";
            ConfigValidationResult result = await ConfigValidationScriptRunner.ExecuteAsync(
                Descriptor(code, root),
                MakeScript(),
                null,
                root);

            Assert.Contains("执行失败", result.Error);
            Assert.Equal("保留", File.ReadAllText(Path.Combine(root, "first.json")));
            Assert.Contains("first.json", result.ChangedFiles);
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public async Task SyntaxAndTimeoutErrorsAreReportedAsNonBlockingResults()
    {
        string root = MakeTempDir();
        try
        {
            ConfigValidationResult syntax = await ConfigValidationScriptRunner.ExecuteAsync(
                Descriptor("const = ;", root),
                MakeScript(),
                null,
                root);
            Assert.Contains("执行失败", syntax.Error);

            ConfigValidationResult timeout = await ConfigValidationScriptRunner.ExecuteAsync(
                Descriptor("while (true) {}", root),
                MakeScript(),
                null,
                root);
            Assert.Contains("超时", timeout.Error);
        }
        finally
        {
            DeleteExact(root);
        }
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
}
