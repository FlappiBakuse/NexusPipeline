using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Automation;
using NexusPipeline.Host.Control.Configuration;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Storage.Emulation;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Serialization.Protocol;

namespace NexusPipeline.Host.Control.Mcp;

[SupportedOSPlatform("windows")]
internal sealed class McpAutomationTools(AutomationUseCases automation, EmulatorTargetService? emulators = null,
    ContractReader? contracts = null, SourceUseCases? sources = null)
{
    internal IReadOnlyList<McpToolDescription> List(AuthenticatedControlRequest request) =>
        Array.AsReadOnly(new[] { "automation_list", "scheduling_list" }.Concat(emulators is null ? [] : new[] { "emulator_discover" })
            .Concat(sources is null ? [] : new[] { "script_discover" }).Where(name =>
            request.Visible().Any(item => item.Id == Capability(name))).Select(name => new McpToolDescription(name,
                name.Replace('_', ' '), name == "script_discover" ?
                    "Read script configuration candidates under an explicit directory through the installed adapter; no save or execution." :
                    "Read saved definitions from the actual catalog owner; this is not a dispatch authorization.",
                name == "emulator_discover" ? McpToolSchemas.EmulatorDiscovery() : name == "script_discover" ?
                    McpToolSchemas.ScriptSource() : McpToolSchemas.Empty, new(true, false, true, false))).ToArray());

    internal async Task<object> DiscoverSourceAsync(AuthenticatedControlRequest request, JsonElement parameters)
    {
        if (sources is null || !parameters.TryGetProperty("arguments", out var arguments))
            throw new McpProtocolException(-32602, "tool_arguments_invalid");
        var result = await sources.DiscoverAsync(request, arguments, CancellationToken.None);
        return new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result, NotificationUseCases.Json) } },
            structuredContent = result, isError = false };
    }

    internal async Task<object> DiscoverAsync(AuthenticatedControlRequest request, JsonElement parameters)
    {
        request.Require(ControlCapability.AutomationRead);
        if (emulators is null || contracts is null || !parameters.TryGetProperty("arguments", out var arguments))
            throw new McpProtocolException(-32602, "tool_arguments_invalid");
        var command = contracts.Read<ControlEmulatorDiscover>(JsonSerializer.SerializeToUtf8Bytes(arguments), "control-emulator-discover.schema.json");
        var result = await emulators.DiscoverAsync(Guid.Parse(command.ScriptId), command.ExpectedCatalogRevision,
            command.RootIds.Select(Guid.Parse).ToArray(), () => request.Require(ControlCapability.AutomationRead), CancellationToken.None);
        return new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result, NotificationUseCases.Json) } }, structuredContent = result, isError = false };
    }
    internal object Call(AuthenticatedControlRequest request, JsonElement parameters)
    {
        if (!parameters.TryGetProperty("name", out var value) || value.ValueKind != JsonValueKind.String ||
            !parameters.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
            throw new McpProtocolException(-32602, "tool_arguments_invalid");
        ControlRequestBody.RequireMembers(arguments);
        var name = value.GetString()!;
        request.Require(Capability(name));
        var result = name == "automation_list" ? automation.Read(request) : automation.Schedules(request);
        return new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result, NotificationUseCases.Json) } },
            structuredContent = result, isError = false };
    }
    private static ControlCapability Capability(string name) => name switch
    {
        "automation_list" => ControlCapability.AutomationRead,
        "emulator_discover" => ControlCapability.AutomationRead,
        "script_discover" => ControlCapability.ConfigurationRead,
        "scheduling_list" => ControlCapability.SchedulingRead,
        _ => throw new McpProtocolException(-32602, "tool_unknown")
    };
}
