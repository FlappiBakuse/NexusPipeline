using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Execution;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Host.Control.Mcp;

[SupportedOSPlatform("windows")]
internal sealed class McpExecutionTools(ExecutionUseCases execution, ContractReader contracts)
{
    internal static readonly string[] Names = ["execution_preview", "execution_start", "execution_cancel"];
    internal IReadOnlyList<McpToolDescription> List(AuthenticatedControlRequest request) =>
        Array.AsReadOnly(Names.Where(name => request.Visible().Any(value => value.Id == Capability(name))).Select(name =>
            new McpToolDescription(name, name.Replace('_', ' '), name == "execution_preview" ?
                "Inspect readiness and bounded resources without accepting a run." :
                name == "execution_start" ? "Durably accept one exact execution selection." :
                    "Close original dispatch or decline a waiting retry; this does not attest writer stop.",
                Schema(name), new(name == "execution_preview", false, name != "execution_preview", true))).ToArray());

    internal object Call(AuthenticatedControlRequest request, JsonElement parameters)
    {
        if (!parameters.TryGetProperty("name", out var nameValue) || nameValue.ValueKind != JsonValueKind.String ||
            !parameters.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
            throw Invalid();
        var name = nameValue.GetString()!;
        request.Require(Capability(name));
        object result;
        if (name == "execution_cancel")
        {
            RequireArguments(arguments, "operationId", "startOperationId", "cancellation");
            var cancellation = arguments.GetProperty("cancellation");
            using var checkedBody = contracts.Read(JsonSerializer.SerializeToUtf8Bytes(cancellation), "control-execution-cancellation.schema.json");
            result = execution.Cancel(request, ControlRequestBody.Uuid(arguments.GetProperty("operationId")),
                ControlRequestBody.Uuid(arguments.GetProperty("startOperationId")),
                ControlRequestBody.Uuid(cancellation.GetProperty("runId")), ControlRequestBody.Uuid(cancellation.GetProperty("attemptId")));
        }
        else
        {
            RequireArguments(arguments, name == "execution_start" ? ["operationId", "selection"] : ["selection"]);
            var selection = arguments.GetProperty("selection");
            using var checkedBody = contracts.Read(JsonSerializer.SerializeToUtf8Bytes(selection), "control-execution-selection.schema.json");
            var selected = ExecutionControlCommand.Selection(selection);
            result = name == "execution_preview" ? execution.Preview(request, selected) :
                execution.Start(request, ControlRequestBody.Uuid(arguments.GetProperty("operationId")), selected);
        }
        return new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result,
            NotificationUseCases.Json) } }, structuredContent = result, isError = false };
    }

    private static ControlCapability Capability(string name) => name switch
    {
        "execution_preview" => ControlCapability.ExecutionPreview,
        "execution_start" => ControlCapability.ExecutionStart,
        "execution_cancel" => ControlCapability.ExecutionCancel,
        _ => throw new McpProtocolException(-32602, "tool_unknown")
    };
    private static JsonElement Schema(string name)
    {
        var properties = new JsonObject();
        if (name != "execution_preview") properties["operationId"] = Uuid();
        if (name == "execution_cancel")
        {
            properties["startOperationId"] = Uuid();
            properties["cancellation"] = JsonNode.Parse(McpToolSchemas.ExecutionCancellation().GetRawText());
        }
        else properties["selection"] = JsonNode.Parse(McpToolSchemas.ExecutionSelection().GetRawText());
        return JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = properties, ["required"] = new JsonArray(properties.Select(item => (JsonNode?)JsonValue.Create(item.Key)).ToArray())
        });
    }
    private static void RequireArguments(JsonElement value, params string[] names)
    {
        ControlRequestBody.RequireMembers(value, names);
        if (names.Any(name => !value.TryGetProperty(name, out _))) throw Invalid();
    }
    private static JsonNode Uuid() => new JsonObject { ["type"] = "string", ["format"] = "uuid" };
    private static McpProtocolException Invalid() => new(-32602, "tool_arguments_invalid");
}
