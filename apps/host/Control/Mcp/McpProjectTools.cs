using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Configuration;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Host.Control.Notifications;

namespace NexusPipeline.Host.Control.Mcp;

[SupportedOSPlatform("windows")]
internal sealed class McpProjectTools(ProjectUseCases projects)
{
    internal static readonly string[] Names = ["project_context", "project_discover", "project_select", "project_result", "project_preview", "project_page", "project_release"];

    internal IReadOnlyList<McpToolDescription> List(AuthenticatedControlRequest request) =>
        Array.AsReadOnly(Names.Where(name => request.Visible().Any(item => item.Id == Capability(name)) &&
            (name != "project_result" || request.Visible().Any(item => item.Id == ControlCapability.OperationsRead)))
            .Select(name => new McpToolDescription(name, name.Replace('_', ' '),
                name == "project_select" ? "Save an explicit compiler source choice with revision and operation identity." :
                "Read bounded compiler discovery or a subject-bound expiring project preview.",
                McpToolSchemas.Project(name["project_".Length..]), new(name != "project_select", false, true, false))).ToArray());

    internal async Task<object> CallAsync(AuthenticatedControlRequest request, JsonElement parameters)
    {
        if (!parameters.TryGetProperty("name", out var nameValue) || nameValue.ValueKind != JsonValueKind.String ||
            !parameters.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object ||
            nameValue.GetString() is not { } name || !Names.Contains(name)) throw new McpProtocolException(-32602, "tool_arguments_invalid");
        request.Require(Capability(name));
        var result = await projects.ExecuteAsync(request, name["project_".Length..], arguments, CancellationToken.None);
        return new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result, NotificationUseCases.Json) } },
            structuredContent = result, isError = false };
    }

    private static ControlCapability Capability(string name) => name == "project_select" ? ControlCapability.ConfigurationEdit : ControlCapability.ConfigurationRead;
}
