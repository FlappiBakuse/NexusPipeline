using System.Text.Json;
using ModelContextProtocol.Protocol;
using NexusPipeline.App.Contracts;
using NexusPipeline.Utilities;

namespace NexusPipeline.Mcp;

/// <summary>把应用层 OperationResult 转成 MCP 工具结果；业务失败留在 CallToolResult 内，不升级为 JSON-RPC 错误。</summary>
internal static class McpToolResult
{
    public static CallToolResult Success(object? data = null)
    {
        return Build(new McpToolEnvelope { Ok = true, Data = data }, isError: false);
    }

    public static CallToolResult From<T>(OperationResult<T> result, Func<T?, object?>? project = null)
    {
        if (result.Succeeded)
        {
            return Success(project is null ? result.Value : project(result.Value));
        }
        OperationError error = result.Error ?? new OperationError(
            "internal_error",
            "操作失败",
            OperationErrorKind.Internal);
        return Failure(error.Code, error.Message, error.Candidates);
    }

    public static CallToolResult Failure(
        string code,
        string message,
        IReadOnlyList<string>? candidates = null)
    {
        return Build(new McpToolEnvelope
        {
            Ok = false,
            ErrorCode = code,
            ErrorMessage = message,
            Candidates = candidates,
        }, isError: true);
    }

    public static CallToolResult Exception(Exception exception)
    {
        return Failure("internal_error", exception.Message);
    }

    private static CallToolResult Build(McpToolEnvelope envelope, bool isError)
    {
        JsonElement json = JsonSerializer.SerializeToElement(envelope, JsonOpts.Web);
        return new CallToolResult
        {
            IsError = isError,
            StructuredContent = json,
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = json.GetRawText() },
            },
        };
    }
}
