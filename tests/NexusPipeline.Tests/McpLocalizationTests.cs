using System.Text.Json;
using NexusPipeline.App.Commands;
using NexusPipeline.App.Contracts;
using NexusPipeline.Cli;
using NexusPipeline.Localization;
using NexusPipeline.Mcp;
using NexusPipeline.Models;
using ModelContextProtocol.Protocol;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class McpLocalizationTests
{
    [Fact]
    public void EnglishMcpValidationFailureDoesNotLeakSourceLanguageMessage()
    {
        using IDisposable locale = LocaleContext.Push(LocaleCatalog.EnglishLocale);
        OperationResult<string> failure = OperationResult<string>.Failure(
            "user_name_invalid",
            "用户名不能为空且不能包含非法字符",
            OperationErrorKind.Validation);

        CallToolResult output = McpToolResult.From(failure);
        string payload = output.StructuredContent?.ToString() ?? "";

        Assert.DoesNotContain("用户名", payload);
        Assert.Contains("username", payload.ToLowerInvariant());
    }

    [Fact]
    public void EnglishMcpMessageArgumentsAreRenderedInLocalizedOutput()
    {
        using IDisposable locale = LocaleContext.Push(LocaleCatalog.EnglishLocale);
        OperationResult<string> failure = OperationResult<string>.Failure(
            "not_found",
            "未找到运行任务：run-1",
            OperationErrorKind.NotFound,
            messageKey: "api.error.run_not_found",
            messageArgs: new Dictionary<string, object?> { ["runId"] = "run-1" });

        string payload = McpToolResult.From(failure).StructuredContent?.ToString() ?? "";

        Assert.Contains("The run was not found: run-1", payload);
        Assert.DoesNotContain("未找到", payload);
    }

    [Fact]
    public void EnglishRealCommandFailuresUseLocalizedUserMessages()
    {
        using IDisposable locale = LocaleContext.Push(LocaleCatalog.EnglishLocale);

        string userPayload = McpToolResult.From(UserCommands.Create("invalid/name", "")).StructuredContent?.ToString() ?? "";
        string settingsPayload = McpToolResult.From(UserCommands.UpdateGlobalSettings(
            "missing-user",
            new UserBindingOverrides
            {
                General = new UserGeneralOverride { SyncEnabled = true, RunDays = -2 },
            })).StructuredContent?.ToString() ?? "";
        string scriptPayload = McpToolResult.From(ScriptCommands.Create(new ScriptInstance())).StructuredContent?.ToString() ?? "";

        Assert.DoesNotContain("用户名", userPayload);
        Assert.Contains("username", userPayload.ToLowerInvariant());
        Assert.DoesNotContain("运行天数", settingsPayload);
        Assert.Contains("run-days", settingsPayload.ToLowerInvariant());
        Assert.DoesNotContain("脚本名称", scriptPayload);
        Assert.Contains("script name", scriptPayload.ToLowerInvariant());
    }

    [Fact]
    public void EnglishCliValidationFailureDoesNotLeakSourceLanguageMessage()
    {
        TextWriter original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            LocaleCatalog.SetHostLocale(LocaleCatalog.EnglishLocale);
            CliOutput.Configure(new[] { "--json" });
            CliOutput.WriteFailure("user_name_invalid", "用户名不能为空且不能包含非法字符");

            JsonDocument document = JsonDocument.Parse(output.ToString());
            string message = document.RootElement.GetProperty("message").GetString() ?? "";
            Assert.DoesNotContain("用户名", message);
            Assert.Contains("username", message.ToLowerInvariant());
        }
        finally
        {
            Console.SetOut(original);
            LocaleCatalog.SetHostLocale(LocaleCatalog.DefaultLocale);
            CliOutput.Configure(Array.Empty<string>());
        }
    }
}
