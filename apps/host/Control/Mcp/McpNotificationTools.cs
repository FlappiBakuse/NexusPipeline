using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Host.Control.Notifications;

namespace NexusPipeline.Host.Control.Mcp;

[SupportedOSPlatform("windows")]
internal sealed class McpNotificationTools(NotificationUseCases notifications)
{
    private sealed record Tool(string Name, string? Action, ControlCapability Capability, JsonElement Schema);
    private readonly Tool[] _tools =
    [
        new("notifications_list", null, ControlCapability.NotificationsRead, McpToolSchemas.Empty),
        .. new[] { "save", "delete", "reorder", "limit", "test" }.Select(action =>
            new Tool("notifications_" + action, action, NotificationUseCases.Capability(action),
                McpToolSchemas.Notification(action)))
    ];

    internal IReadOnlyList<McpToolDescription> List(AuthenticatedControlRequest request)
    {
        var visible = request.Visible().Select(capability => capability.Id).ToHashSet();
        return Array.AsReadOnly(_tools.Where(tool => visible.Contains(tool.Capability)).Select(tool =>
            new McpToolDescription(tool.Name, tool.Name.Replace('_', ' '),
                tool.Action == "test" ? "Queue a test of the exact saved notification task and revision." :
                    tool.Action is null ? "Read the saved notification catalog without credentials." : "Change the saved notification catalog.",
                tool.Schema, new(tool.Action is null, tool.Action == "delete", true, tool.Action == "test"))).ToArray());
    }

    internal async Task<object> CallAsync(AuthenticatedControlRequest request, JsonElement parameters)
    {
        ControlRequestBody.RequireMembers(parameters, "name", "arguments");
        if (!parameters.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
            !parameters.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
            throw new McpProtocolException(-32602, "tool_arguments_invalid");
        var tool = _tools.SingleOrDefault(item => item.Name == name.GetString()) ??
            throw new McpProtocolException(-32602, "tool_unknown");
        request.Require(tool.Capability);
        object result;
        if (tool.Action is null)
        {
            ControlRequestBody.RequireMembers(arguments);
            result = notifications.Read(request);
        }
        else
        {
            using var command = NotificationControlCommand.Read(arguments);
            if (command.Action != tool.Action) throw new McpProtocolException(-32602, "tool_action_mismatch");
            result = await notifications.ExecuteAsync(request, command);
        }
        return new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(result, NotificationUseCases.Json) } },
            structuredContent = result, isError = false
        };
    }

    internal static object Failure(string code) => new
        { content = new[] { new { type = "text", text = code } }, isError = true };
}
