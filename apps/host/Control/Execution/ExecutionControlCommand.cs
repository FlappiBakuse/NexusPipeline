using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Host.Control.Http;

namespace NexusPipeline.Host.Control.Execution;

internal static class ExecutionControlCommand
{
    internal static ExecutionSelectionRequest Selection(JsonElement root)
    {
        ControlRequestBody.RequireMembers(root, "format", "version", "target", "expectedCatalogRevision", "expectedSettingsRevision");
        if (root.GetProperty("format").GetString() != "nexus.control.execution-selection" ||
            root.GetProperty("version").GetInt32() != 1) throw Invalid();
        var target = root.GetProperty("target");
        if (target.ValueKind != JsonValueKind.Object || !target.TryGetProperty("kind", out var kind) ||
            kind.ValueKind != JsonValueKind.String) throw Invalid();
        NexusPipeline.Core.Execution.ExecutionTarget selected = kind.GetString() switch
        {
            "binding" => Binding(target),
            "projectInitialization" => new ProjectInitializationTarget(Binding(target).BindingId),
            "script" => Script(target),
            "scriptUser" => ScriptUser(target),
            "queue" => Queue(target),
            _ => throw Invalid()
        };
        if (!root.GetProperty("expectedCatalogRevision").TryGetInt64(out var catalog) || catalog < 0 ||
            !root.GetProperty("expectedSettingsRevision").TryGetInt64(out var settings) || settings < 0) throw Invalid();
        return new(selected, catalog, settings);
    }
    private static BindingTarget Binding(JsonElement target)
    { ControlRequestBody.RequireMembers(target, "kind", "bindingId"); return new(ControlRequestBody.Uuid(target.GetProperty("bindingId"))); }
    private static ScriptTarget Script(JsonElement target)
    { ControlRequestBody.RequireMembers(target, "kind", "scriptId"); return new(ControlRequestBody.Uuid(target.GetProperty("scriptId"))); }
    private static ScriptUserTarget ScriptUser(JsonElement target)
    {
        ControlRequestBody.RequireMembers(target, "kind", "scriptId", "userId");
        return new(ControlRequestBody.Uuid(target.GetProperty("scriptId")), ControlRequestBody.Uuid(target.GetProperty("userId")));
    }
    private static QueueTarget Queue(JsonElement target)
    { ControlRequestBody.RequireMembers(target, "kind", "queueId"); return new(ControlRequestBody.Uuid(target.GetProperty("queueId"))); }
    private static ControlRequestException Invalid() => new(400, "execution_request_invalid");
}
