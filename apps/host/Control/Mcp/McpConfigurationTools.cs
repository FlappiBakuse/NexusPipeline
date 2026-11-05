using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Configuration;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Host.Control.Notifications;

namespace NexusPipeline.Host.Control.Mcp;

[SupportedOSPlatform("windows")]
internal sealed class McpConfigurationTools(ValueUseCases values, ImportUseCases imports)
{
    internal IReadOnlyList<McpToolDescription> List(AuthenticatedControlRequest request)
    {
        var tools = new List<McpToolDescription>();
        if (request.Visible().Any(item => item.Id == ControlCapability.ConfigurationRead))
            tools.Add(new("configuration_resources", "Configuration resources",
                "Read declared saved Generic resource metadata.",
                JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false,
                    required = new[] { "bindingId" }, properties = new { bindingId = new { type = "string", format = "uuid" } } }),
                new(true, false, true, false)));
        if (request.Visible().Any(item => item.Id == ControlCapability.ConfigurationEdit))
            tools.Add(new("configuration_import", "Import configuration",
                "Read declared source files into revision-bound account snapshots without changing source files or starting an editor.",
                McpToolSchemas.ConfigurationImport(), new(false, false, true, false)));
        return tools.AsReadOnly();
    }
    internal object Call(AuthenticatedControlRequest request, JsonElement parameters)
    {
        if (!parameters.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("bindingId", out var binding))
            throw new McpProtocolException(-32602, "tool_arguments_invalid");
        object result;
        if (parameters.GetProperty("name").GetString() == "configuration_import")
            result = imports.Execute(request, arguments, CancellationToken.None);
        else
        {
            ControlRequestBody.RequireMembers(arguments, "bindingId");
            request.Require(ControlCapability.ConfigurationRead);
            result = values.Read(request, ControlRequestBody.Uuid(binding));
        }
        return new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result, NotificationUseCases.Json) } },
            structuredContent = result, isError = false };
    }
}
