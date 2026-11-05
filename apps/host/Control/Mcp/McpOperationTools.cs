using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Operations;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Host.Control.Notifications;

namespace NexusPipeline.Host.Control.Mcp;

[SupportedOSPlatform("windows")]
internal sealed class McpOperationTools(IOperationQueries operations, IReadOnlySet<ControlCapability> available)
{
    internal IReadOnlyList<McpToolDescription> List(AuthenticatedControlRequest request) =>
        request.Visible().Any(item => item.Id == ControlCapability.OperationsRead)
        ? [new("capabilities", "Capabilities", "Read currently available shared control capabilities.",
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false }),
            new(true, false, true, false)), new("operation_read", "Read operation", "Read an operation belonging to the authenticated server subject.",
            JsonSerializer.SerializeToElement(new { type = "object", required = new[] { "operationId" }, additionalProperties = false,
                properties = new { operationId = new { type = "string", format = "uuid" } } }), new(true, false, true, false))]
        : [];
    internal object Call(AuthenticatedControlRequest request, JsonElement parameters)
    {
        if (!parameters.TryGetProperty("name", out var tool)) throw new McpProtocolException(-32602, "tool_arguments_invalid");
        if (tool.GetString() == "capabilities")
        {
            request.Require(ControlCapability.CapabilitiesRead);
            if (!parameters.TryGetProperty("arguments", out var empty)) throw new McpProtocolException(-32602, "tool_arguments_invalid");
            ControlRequestBody.RequireMembers(empty);
            return Result(new { capabilities = request.Visible().Where(item => available.Contains(item.Id)).Select(item => item.Name).ToArray() });
        }
        request.Require(ControlCapability.OperationsRead);
        if (!parameters.TryGetProperty("arguments", out var args)) throw new McpProtocolException(-32602, "tool_arguments_invalid");
        ControlRequestBody.RequireMembers(args, "operationId");
        if (!args.TryGetProperty("operationId", out var idValue)) throw new McpProtocolException(-32602, "tool_arguments_invalid");
        var id = ControlRequestBody.Uuid(idValue);
        var value = operations.Read(id, request.SubjectId);
        return Result(new { found = value is not null, operation = value });
    }
    private static object Result(object value) => new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(value,
        NotificationUseCases.Json) } }, isError = false };
}
