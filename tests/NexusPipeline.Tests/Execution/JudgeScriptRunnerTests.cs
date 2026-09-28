using System.Text.Json;
using Xunit;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Shared.Localization;

namespace NexusPipeline.Tests.Execution;

/// <summary>判断脚本执行器：扩展名映射、路径边界、输入契约与内置 JS 执行。</summary>
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
    public void ExtensionMappingResolveWithinAndFileCollectionStayBounded()
    {
        Assert.True(JudgeScriptRunner.IsJudgeExtension("judge.JS"));
        Assert.Equal("python", JudgeScriptRunner.LanguageOfExtension("judge.PY"));

        Assert.Equal(Path.Combine(_scriptDir, "nested", "result.txt"), JudgeScriptRunner.ResolveWithin(_scriptDir, "nested/result.txt"));
        Assert.Null(JudgeScriptRunner.ResolveWithin(_scriptDir, "..\\escape.txt"));
        Assert.Null(JudgeScriptRunner.ResolveWithin(_scriptDir, Path.Combine(Path.GetTempPath(), "escape.txt")));

        List<JudgeScriptInputFile> files = JudgeScriptRunner.CollectFiles(_configDir, _scriptDir);
        Assert.Contains(files, file => file.Root == "config" && file.Path == "settings.json");
        Assert.Contains(files, file => file.Root == "script" && file.Path == "input.txt");
    }

    [Fact]
    public void BuildInputSerializesCurrentAttemptContract()
    {
        var script = new ScriptInstance { Id = "script-1", Name = "判断脚本测试", ConfigPath = _configFile, RootPath = _root };
        var user = new ResolvedScriptUser(
            "user-id",
            "用户甲",
            new UserScriptBinding { ScriptInstanceId = script.Id, Enabled = true });

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
        Assert.Equal(_scriptDir, root.GetProperty("scriptDir").GetString());
        Assert.Equal(LocaleCatalog.HostLocale, root.GetProperty("locale").GetString());
        Assert.True(root.GetProperty("files").GetArrayLength() >= 2);
        Assert.Equal(0, root.GetProperty("screenshots").GetArrayLength());
    }

    [Fact]
    public async Task JavaScriptCanWriteInsideScriptRootAndPartialRequiresReason()
    {
        var script = new ScriptInstance
        {
            JudgeScriptLanguage = "javascript",
            JudgeScript = "var dir = JSON.parse(__NEXUS_INPUT__).scriptDir; var abs = dir + '\\\\result.txt'; nexus.writeFile('result.txt', 'written'); console.log(JSON.stringify({status:'success', reason: nexus.readFile(abs)}));",
        };
        List<JudgeScriptInputFile> files = JudgeScriptRunner.CollectFiles(_configFile, _scriptDir);
        string input = JudgeScriptRunner.BuildInput(script, null, files, _scriptDir, "", false);

        JudgeScriptResult result = await JudgeScriptRunner.ExecuteAsync(
            script, input, files, _configFile, _scriptDir, CancellationToken.None);

        Assert.Null(result.JudgeError);
        Assert.Equal("success", result.Status);
        Assert.Equal("written", File.ReadAllText(Path.Combine(_scriptDir, "result.txt")));

        var escaping = new ScriptInstance
        {
            JudgeScriptLanguage = "javascript",
            JudgeScript = "nexus.writeFile('../escape.txt', 'blocked'); console.log('{\"status\":\"success\",\"reason\":\"boundary\"}');",
        };
        await JudgeScriptRunner.ExecuteAsync(escaping, input, files, _configFile, _scriptDir, CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));

        var partial = new ScriptInstance
        {
            JudgeScriptLanguage = "javascript",
            JudgeScript = "console.log(JSON.stringify({status:'partial', reason:'some tasks incomplete', replaceConfigs:['ignored.txt']}));",
        };
        JudgeScriptResult partialResult = await JudgeScriptRunner.ExecuteAsync(
            partial,
            JudgeScriptRunner.BuildInput(partial, null, [], _scriptDir, "", false),
            [],
            _configFile,
            _scriptDir,
            CancellationToken.None);

        Assert.Equal("partial", partialResult.Status);
        Assert.Equal(new[] { "ignored.txt" }, partialResult.ReplaceConfigs);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响契约断言。
        }
    }
}
