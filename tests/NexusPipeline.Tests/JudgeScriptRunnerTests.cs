using System.Text.Json;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>判断脚本执行器组件测试：覆盖扩展名、路径边界、输入契约和内置 JS 执行。</summary>
public sealed class JudgeScriptRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-judge-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _configDir;
    private readonly string _scriptDir;
    private readonly string _configFile;

    public JudgeScriptRunnerTests()
    {
        _configDir = Path.Combine(_root, "config");
        _scriptDir = Path.Combine(_root, "script");
        _configFile = Path.Combine(_configDir, "settings.json");
        Directory.CreateDirectory(_configDir);
        Directory.CreateDirectory(_scriptDir);
        File.WriteAllText(_configFile, "{\"enabled\":true}");
        File.WriteAllText(Path.Combine(_scriptDir, "input.txt"), "script-input");
    }

    [Fact]
    public void ExtensionAndLanguageMappingIsStable()
    {
        Assert.True(JudgeScriptRunner.IsJudgeExtension("judge.JS"));
        Assert.True(JudgeScriptRunner.IsJudgeExtension("judge.py"));
        Assert.False(JudgeScriptRunner.IsJudgeExtension("judge.txt"));
        Assert.Equal("javascript", JudgeScriptRunner.LanguageOfExtension("judge.js"));
        Assert.Equal("python", JudgeScriptRunner.LanguageOfExtension("judge.PY"));
    }

    [Fact]
    public void ResolveWithinRejectsRootedAndEscapingPaths()
    {
        string safe = JudgeScriptRunner.ResolveWithin(_scriptDir, "nested/result.txt")!;

        Assert.Equal(Path.Combine(_scriptDir, "nested", "result.txt"), safe);
        Assert.Null(JudgeScriptRunner.ResolveWithin(_scriptDir, "..\\escape.txt"));
        Assert.Null(JudgeScriptRunner.ResolveWithin(_scriptDir, Path.Combine(Path.GetTempPath(), "escape.txt")));
        Assert.Null(JudgeScriptRunner.ResolveWithin(_scriptDir, ""));
    }

    [Fact]
    public void CollectFilesKeepsConfigAndScriptRootsSeparate()
    {
        List<JudgeScriptInputFile> files = JudgeScriptRunner.CollectFiles(_configDir, _scriptDir);

        Assert.Contains(files, file => file.Root == "config" && file.Path == "settings.json");
        Assert.Contains(files, file => file.Root == "script" && file.Path == "input.txt");
        Assert.All(files, file => Assert.True(Path.IsPathRooted(file.Abs)));
    }

    [Fact]
    public void BuildInputSerializesCurrentAttemptContract()
    {
        var script = new ScriptInstance
        {
            Id = "script-1",
            Name = "判断脚本测试",
            ConfigPath = _configFile,
            RootPath = _root,
        };
        var user = new ResolvedScriptUser(
            "user-id",
            "用户甲",
            new UserScriptBinding
            {
                ScriptInstanceId = script.Id,
                Enabled = true,
            });
        string input = JudgeScriptRunner.BuildInput(
            script,
            user,
            JudgeScriptRunner.CollectFiles(_configFile, _scriptDir),
            _scriptDir,
            "当前尝试日志",
            logTruncated: false);

        using JsonDocument document = JsonDocument.Parse(input);
        JsonElement root = document.RootElement;
        Assert.Equal("script-1", root.GetProperty("script").GetProperty("Id").GetString());
        Assert.Equal("用户甲", root.GetProperty("user").GetProperty("UserName").GetString());
        Assert.Equal("当前尝试日志", root.GetProperty("log").GetString());
        Assert.False(root.GetProperty("logTruncated").GetBoolean());
        Assert.Equal(_scriptDir, root.GetProperty("scriptDir").GetString());
        Assert.True(root.GetProperty("files").GetArrayLength() >= 2);
        Assert.Equal(0, root.GetProperty("screenshots").GetArrayLength());
    }

    [Fact]
    public void BuildInputIncludesScreenshotMetadataWithoutImageBytes()
    {
        var metadata = new RunScreenshotMetadata(
            "screenshot-0000000001",
            1,
            DateTimeOffset.Parse("2026-09-01T10:00:00+08:00"),
            2,
            1920,
            1080,
            "pc",
            "judge-manual");

        string input = JudgeScriptRunner.BuildInput(
            new ScriptInstance(),
            null,
            [],
            _scriptDir,
            "",
            false,
            [metadata]);

        using JsonDocument document = JsonDocument.Parse(input);
        JsonElement screenshot = document.RootElement.GetProperty("screenshots")[0];
        Assert.Equal("screenshot-0000000001", screenshot.GetProperty("Id").GetString());
        Assert.Equal(1920, screenshot.GetProperty("Width").GetInt32());
        Assert.False(screenshot.TryGetProperty("Data", out _));
    }

    [Fact]
    public async Task JavaScriptCanReturnResultAndWriteOnlyInsideScriptRoot()
    {
        string safeFile = Path.Combine(_scriptDir, "result.txt");
        string escapedPath = JsonSerializer.Serialize(safeFile);
        var script = new ScriptInstance
        {
            JudgeScriptLanguage = "javascript",
            JudgeScript = $"var input = JSON.parse(__NEXUS_INPUT__); nexus.writeFile('result.txt', 'written'); console.log(JSON.stringify({{status:'success', reason:nexus.readFile({escapedPath})}}));",
        };
        string input = JudgeScriptRunner.BuildInput(script, null, JudgeScriptRunner.CollectFiles(_configFile, _scriptDir), _scriptDir, "", false);

        JudgeScriptResult result = await JudgeScriptRunner.ExecuteAsync(
            script,
            input,
            JudgeScriptRunner.CollectFiles(_configFile, _scriptDir),
            _configFile,
            _scriptDir,
            CancellationToken.None);

        Assert.Null(result.JudgeError);
        Assert.Equal("success", result.Status);
        Assert.Equal("written", result.Reason);
        Assert.Equal("written", File.ReadAllText(safeFile));

        var escapingScript = new ScriptInstance
        {
            JudgeScriptLanguage = "javascript",
            JudgeScript = "nexus.writeFile('../escape.txt', 'blocked'); console.log('{\"status\":\"success\",\"reason\":\"boundary\"}');",
        };
        await JudgeScriptRunner.ExecuteAsync(
            escapingScript,
            input,
            JudgeScriptRunner.CollectFiles(_configFile, _scriptDir),
            _configFile,
            _scriptDir,
            CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public async Task JavaScriptCanReturnPartialResult()
    {
        var script = new ScriptInstance
        {
            JudgeScriptLanguage = "javascript",
            JudgeScript = "console.log(JSON.stringify({status:'partial', reason:'some tasks incomplete', replaceConfigs:['ignored.txt']}));",
        };
        string input = JudgeScriptRunner.BuildInput(script, null, [], _scriptDir, "", false);

        JudgeScriptResult result = await JudgeScriptRunner.ExecuteAsync(
            script,
            input,
            [],
            _configFile,
            _scriptDir,
            CancellationToken.None);

        Assert.Null(result.JudgeError);
        Assert.Equal("partial", result.Status);
        Assert.Equal("some tasks incomplete", result.Reason);
        Assert.Equal(new[] { "ignored.txt" }, result.ReplaceConfigs);
    }

    [Fact]
    public async Task PartialWithoutReasonIsNotAValidResult()
    {
        var script = new ScriptInstance
        {
            JudgeScriptLanguage = "javascript",
            JudgeScript = "console.log(JSON.stringify({status:'partial'}));",
        };
        string input = JudgeScriptRunner.BuildInput(script, null, [], _scriptDir, "", false);

        JudgeScriptResult result = await JudgeScriptRunner.ExecuteAsync(
            script,
            input,
            [],
            _configFile,
            _scriptDir,
            CancellationToken.None);

        Assert.Null(result.JudgeError);
        Assert.Equal("", result.Status);
    }

    [Fact]
    public async Task JavaScriptCanCaptureScreenshotAndReturnSelectedId()
    {
        var screenshot = new RunScreenshot(
            "screenshot-0000000001",
            1,
            DateTimeOffset.Now,
            1,
            640,
            480,
            "pc",
            "judge-manual",
            new byte[] { 1, 2, 3 });
        int calls = 0;
        var script = new ScriptInstance
        {
            JudgeScriptLanguage = "javascript",
            JudgeScript = "console.log(JSON.stringify({status:'success', reason:'captured', notifyScreenshotId:nexus.captureScreenshot()}));",
        };
        string input = JudgeScriptRunner.BuildInput(script, null, [], _scriptDir, "", false);

        JudgeScriptResult result = await JudgeScriptRunner.ExecuteAsync(
            script,
            input,
            [],
            _configFile,
            _scriptDir,
            CancellationToken.None,
            _ =>
            {
                calls++;
                return Task.FromResult<RunScreenshot?>(screenshot);
            });

        Assert.Null(result.JudgeError);
        Assert.Equal("success", result.Status);
        Assert.Equal("screenshot-0000000001", result.NotifyScreenshotId);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PythonCanCaptureScreenshotThroughTemporaryLoopbackApi()
    {
        var screenshot = new RunScreenshot(
            "screenshot-0000000002",
            2,
            DateTimeOffset.Now,
            1,
            1280,
            720,
            "emulator",
            "judge-manual",
            new byte[] { 1, 2, 3 });
        int calls = 0;
        var script = new ScriptInstance
        {
            JudgeScriptLanguage = "python",
            JudgeScript = """
                import json
                import sys
                import urllib.request

                with open(sys.argv[1], encoding="utf-8") as stream:
                    input_data = json.load(stream)
                api = input_data["screenshotApi"]
                request = urllib.request.Request(
                    api["endpoint"],
                    method="POST",
                    headers={"X-Nexus-Screenshot-Token": api["token"]},
                )
                with urllib.request.urlopen(request, timeout=5) as response:
                    screenshot_id = json.load(response)["id"]
                print(json.dumps({
                    "status": "success",
                    "reason": "captured",
                    "notifyScreenshotId": screenshot_id,
                }))
                """,
        };
        string input = JudgeScriptRunner.BuildInput(script, null, [], _scriptDir, "", false);

        JudgeScriptResult result = await JudgeScriptRunner.ExecuteAsync(
            script,
            input,
            [],
            _configFile,
            _scriptDir,
            CancellationToken.None,
            _ =>
            {
                calls++;
                return Task.FromResult<RunScreenshot?>(screenshot);
            });

        Assert.Null(result.JudgeError);
        Assert.Equal("success", result.Status);
        Assert.Equal("screenshot-0000000002", result.NotifyScreenshotId);
        Assert.Equal(1, calls);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响契约断言；系统清理会回收测试目录。
        }
    }
}
