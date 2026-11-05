using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Configuration;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class ProjectFrontendControl(Guid instanceId, ControlAuthentication authority, ExtensionFrontendControl frontends,
    ProjectUseCases projects, ContractReader contracts)
{
    internal void Map(WebApplication app)
    {
        app.MapPost("/control/v1/extensions/{extensionId}/frontend/{activationId}/project/{action}",
            async (HttpContext context, string extensionId, string activationId, string action) =>
        {
            if (action is not ("context" or "discover" or "select" or "result" or "preview" or "page" or "release"))
                throw new ControlRequestException(404, "control_not_found");
            var capability = action == "select" ? ControlCapability.ConfigurationEdit : ControlCapability.ConfigurationRead;
            var caller = authority.Require(context, capability); caller.Require(ControlCapability.ExtensionsRead);
            if (action == "result") caller.Require(ControlCapability.OperationsRead);
            var client = frontends.Clients.Read(context, caller) ?? throw new UnauthorizedAccessException("project_frontend_client_required");
            var frontend = frontends.Current(extensionId, activationId);
            new ControlQueryValues(context.Request.Query).Validate();
            using var body = await ControlRequestBody.ReadAsync(context, 131072);
            body.RequireSchema(contracts, "control-project-surface.schema.json");
            var surface = body.Root.GetProperty("context");
            if (ControlRequestBody.Uuid(surface.GetProperty("instanceId")) != instanceId)
                throw new UnauthorizedAccessException("project_frontend_instance_unbound");
            var ownerId = ControlRequestBody.Uuid(surface.GetProperty("entityId"));
            var isScript = surface.GetProperty("surfaceId").GetString() == "automation.script.editor";
            if ((action == "select") != body.Root.TryGetProperty("operationId", out _))
                throw new ControlRequestException(400, "project_operation_identity_invalid");
            void Caller()
            {
                caller.Require(capability);
                caller.Require(ControlCapability.ExtensionsRead);
                client.RequireCurrent();
                if (action == "result") caller.Require(ControlCapability.OperationsRead);
                if (!ReferenceEquals(frontend, frontends.Current(extensionId, activationId)))
                    throw new UnauthorizedAccessException("project_frontend_activation_unbound");
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, client.Lifetime);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await frontend.UseProjectAsync(async (current, cancellation) =>
            {
                var selected = isScript
                    ? ScriptOwner(projects.ReadScriptContext(caller, ownerId, extensionId, current))
                    : BindingOwner(projects.ReadContext(caller, ownerId, extensionId, current, action is not ("select" or "result")));
                if (action == "context")
                {
                    ControlRequestBody.RequireMembers(body.Root.GetProperty("input")); current(); return selected.Context;
                }
                var command = Command(action, selected, body.Root);
                var subject = "project-ui:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                    caller.SubjectId + "\n" + client.ClientId.ToString("D") + "\n" + surface.GetProperty("surfaceId").GetString() + "\n" + ownerId.ToString("D"))));
                return await projects.ExecuteAsync(caller, action, command, cancellation, current, subject, client.Lifetime);
            }, Caller, deadline.Token, client.Lifetime);
            return Results.Json(result, NotificationUseCases.Json);
        });
    }

    private sealed record Owner(Guid Id, long Revision, string ExtensionId, string ProviderId, bool IsScript, object Context);
    private static Owner ScriptOwner(ScriptProjectContext value) => new(value.ScriptId, value.ScriptRevision, value.ExtensionId, value.ProviderId, true, value);
    private static Owner BindingOwner(ProjectEditorContext value) => new(value.BindingId, value.BindingRevision, value.ExtensionId, value.ProviderId, false, value);

    private static JsonElement Command(string action, Owner selected, JsonElement body)
    {
        var input = body.GetProperty("input");
        var revisionName = selected.IsScript ? "expectedScriptRevision" : "expectedBindingRevision";
        var names = action switch
        {
            "select" => new[] { revisionName, "expectedSelectionRevision", "selection", "protectedInputs", "providerId" },
            "result" => ["operationId", revisionName, "expectedSelectionRevision", "selection", "protectedInputs", "providerId", "finish"],
            "discover" => [revisionName, "selection"], "preview" => [revisionName, "selection"],
            "page" => ["previewId", "asset"], _ => ["previewId"]
        };
        if (selected.IsScript) names = names.Where(name => name is not ("protectedInputs" or "finish")).ToArray();
        ControlRequestBody.RequireMembers(input, names);
        if (names.Where(name => name is not ("protectedInputs" or "providerId" or "finish") && (action != "preview" || name != "selection"))
            .Any(name => !input.TryGetProperty(name, out _)))
            throw new ControlRequestException(400, "project_input_invalid");
        var value = JsonNode.Parse(input.GetRawText())!.AsObject();
        if (action is "select" or "result")
        {
            if (input.TryGetProperty("providerId", out var provider) &&
                (provider.ValueKind != JsonValueKind.String || provider.GetString() != selected.ProviderId))
                throw new ControlRequestException(409, "project_provider_changed");
            value["providerId"] = selected.ProviderId;
        }
        value["format"] = "nexus.control.project-command"; value["version"] = 1; value["action"] = action;
        if (action is "select" or "result" or "discover" or "preview")
        {
            if (!input.GetProperty(revisionName).TryGetInt64(out var revision) || revision < 1 ||
                action != "result" && revision != selected.Revision)
                throw new ControlRequestException(409, selected.IsScript ? "project_script_changed" : "project_binding_changed");
            value[selected.IsScript ? "scriptId" : "bindingId"] = selected.Id.ToString("D"); value["extensionId"] = selected.ExtensionId;
        }
        if (action == "select") value["operationId"] = body.GetProperty("operationId").GetString();
        return JsonSerializer.SerializeToElement(value);
    }
}

