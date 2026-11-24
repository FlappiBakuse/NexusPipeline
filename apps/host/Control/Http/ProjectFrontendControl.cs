using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Host.Control.Configuration;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class ProjectFrontendControl(Guid instanceId, ControlAuthentication authority, ExtensionFrontendControl frontends,
    ProjectUseCases projects, ContractReader contracts, BindingDrafts drafts)
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
            ProjectBindingDraft? draft = null;
            Guid? candidate = null;
            if (body.Root.TryGetProperty("draftBinding", out var target))
            {
                if (isScript) throw new ControlRequestException(400, "project_draft_surface_invalid");
                caller.Require(ControlCapability.AutomationWrite);
                var owner = new DraftBindingIdentity(ControlRequestBody.Uuid(target.GetProperty("userId")),
                    ControlRequestBody.Uuid(target.GetProperty("scriptId")), target.GetProperty("scriptRevision").GetInt64());
                candidate = target.TryGetProperty("candidateId", out var id) ? ControlRequestBody.Uuid(id) : null;
                draft = candidate is { } previous ? new(owner, drafts.ReadManaged(previous, caller.SubjectId, ownerId, owner),
                    drafts.ReadProtections(previous, caller.SubjectId, ownerId, owner), previous) : new(owner, null);
            }
            if ((action == "select") != body.Root.TryGetProperty("operationId", out _))
                throw new ControlRequestException(400, "project_operation_identity_invalid");
            void Caller()
            {
                caller.Require(capability);
                caller.Require(ControlCapability.ExtensionsRead);
                if (draft is not null) caller.Require(ControlCapability.AutomationWrite);
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
                    : BindingOwner(projects.ReadContext(caller, ownerId, extensionId, current, action is not ("select" or "result"), draft));
                var input = body.Root.GetProperty("input");
                if (action == "context")
                {
                    ControlRequestBody.RequireMembers(input); current(); return selected.Context;
                }
                var subject = "project-ui:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                    caller.SubjectId + "\n" + client.ClientId.ToString("D") + "\n" + surface.GetProperty("surfaceId").GetString() + "\n" + ownerId.ToString("D") +
                    (draft is null ? "" : $"\n{draft.Owner.UserId:D}\n{draft.Owner.ScriptId:D}\n{draft.Owner.ScriptRevision}\n{candidate:D}"))));
                using var commandBytes = new ControlJsonBuffer(JsonMarshal.GetRawUtf8Value(input).Length + 4096,
                    static () => new JsonException("invalid_json_size"));
                WriteCommand(commandBytes, action, selected, input, body.Root);
                using var command = JsonDocument.Parse(commandBytes.Written);
                return await projects.ExecuteAsync(caller, ProjectUseCases.Command.Borrow(action, command.RootElement),
                    cancellation, current, subject, client.Lifetime, draft, drafts);
            }, Caller, deadline.Token, client.Lifetime);
            return Results.Json(result, NotificationUseCases.Json);
        });
    }

    private sealed record Owner(Guid Id, long Revision, string ExtensionId, string ProviderId, bool IsScript, object Context);
    private static Owner ScriptOwner(ScriptProjectContext value) => new(value.ScriptId, value.ScriptRevision, value.ExtensionId, value.ProviderId, true, value);
    private static Owner BindingOwner(ProjectEditorContext value) => new(value.BindingId, value.BindingRevision, value.ExtensionId, value.ProviderId, false, value);

    private static void WriteCommand(IBufferWriter<byte> output, string action, Owner selected, JsonElement input, JsonElement body)
    {
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
        var requiresProvider = action is "select" or "result";
        var hasProvider = input.TryGetProperty("providerId", out var provider);
        if (requiresProvider && hasProvider && (provider.ValueKind != JsonValueKind.String || provider.GetString() != selected.ProviderId))
            throw new ControlRequestException(409, "project_provider_changed");
        var bindsProject = action is "select" or "result" or "discover" or "preview";
        if (bindsProject && (!input.GetProperty(revisionName).TryGetInt64(out var revision) || revision < 1 ||
            action != "result" && revision != selected.Revision))
            throw new ControlRequestException(409, selected.IsScript ? "project_script_changed" : "project_binding_changed");

        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        foreach (var member in input.EnumerateObject())
        {
            writer.WritePropertyName(member.Name);
            writer.WriteRawValue(JsonMarshal.GetRawUtf8Value(member.Value), skipInputValidation: true);
        }
        if (requiresProvider && !hasProvider) writer.WriteString("providerId", selected.ProviderId);
        writer.WriteString("format", "nexus.control.project-command");
        writer.WriteNumber("version", 1);
        writer.WriteString("action", action);
        if (bindsProject)
        {
            writer.WriteString(selected.IsScript ? "scriptId" : "bindingId", selected.Id);
            writer.WriteString("extensionId", selected.ExtensionId);
        }
        if (action == "select") writer.WriteString("operationId", body.GetProperty("operationId").GetString());
        writer.WriteEndObject();
    }
}

