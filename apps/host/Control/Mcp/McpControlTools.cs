using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Host.Control.Http;

namespace NexusPipeline.Host.Control.Mcp;

[SupportedOSPlatform("windows")]
internal sealed class McpControlTools(McpNotificationTools notifications, McpRecordTools records, McpExecutionTools execution, McpAutomationTools automation, McpConfigurationTools configuration, McpProjectTools projects, McpOperationTools operations)
{
    internal object List(AuthenticatedControlRequest request) =>
        new { tools = notifications.List(request).Concat(records.List(request)).Concat(execution.List(request)).Concat(automation.List(request)).Concat(configuration.List(request)).Concat(projects.List(request)).Concat(operations.List(request)).ToArray() };
    internal Task<object> CallAsync(AuthenticatedControlRequest request, JsonElement parameters)
    {
        ControlRequestBody.RequireMembers(parameters, "name", "arguments");
        if (!parameters.TryGetProperty("name", out var value) || value.ValueKind != JsonValueKind.String)
            throw new McpProtocolException(-32602, "tool_arguments_invalid");
        var name = value.GetString();
        if (name == "emulator_discover") return automation.DiscoverAsync(request, parameters);
        if (name == "script_discover") return automation.DiscoverSourceAsync(request, parameters);
        if (name is not null && McpProjectTools.Names.Contains(name)) return projects.CallAsync(request, parameters);
        if (name is "operation_read" or "capabilities") return Task.FromResult(operations.Call(request, parameters));
        if (name is "configuration_resources" or "configuration_import") return Task.FromResult(configuration.Call(request, parameters));
        if (name is "automation_list" or "scheduling_list")
            return Task.FromResult(automation.Call(request, parameters));
        if (name is "execution_preview" or "execution_start" or "execution_cancel")
            return Task.FromResult(execution.Call(request, parameters));
        return name is "notifications_list" or "notifications_save" or "notifications_delete" or
            "notifications_reorder" or "notifications_limit" or "notifications_test"
                ? notifications.CallAsync(request, parameters) : Task.FromResult(records.Call(request, parameters));
    }
}
