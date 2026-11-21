using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Host.Control.Configuration;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class AdapterControl(ControlAuthentication authority, AdapterUseCases adapters, ContractReader contracts)
{
    internal void Map(WebApplication app)
    {
        app.MapPost("/control/v1/adapters/{action}", async (HttpContext context, string action) =>
        {
            if (action is not ("discover" or "propose" or "previewRepair" or "applyRepair"))
                throw new ControlRequestException(404, "control_not_found");
            var request = authority.Require(context, action is "previewRepair" or "applyRepair" ?
                ControlCapability.ConfigurationRepair : ControlCapability.ConfigurationRead);
            new ControlQueryValues(context.Request.Query).Validate();
            using var body = await ControlRequestBody.ReadAsync(context);
            body.RequireSchema(contracts, "control-adapter-command.schema.json");
            if (body.Root.GetProperty("action").GetString() != action)
                throw new ControlRequestException(400, "adapter_action_mismatch");
            var result = await adapters.ExecuteAsync(request, action, body.Root,
                action == "applyRepair" ? CancellationToken.None : context.RequestAborted);
            return Results.Json(result, NotificationUseCases.Json);
        });
    }

    internal void MapDraftDiscovery(WebApplication app, TaskAdapterService service, BindingDrafts drafts)
    {
        app.MapPost("/control/v1/adapters/discover-draft", async (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.ConfigurationRead);
            new ControlQueryValues(context.Request.Query).Validate();
            using var body = await ControlRequestBody.ReadAsync(context, 4096);
            var root = body.Root;
            ControlRequestBody.RequireMembers(root, "operationId", "bindingId", "candidateId", "userId", "scriptId", "scriptRevision");
            Guid Id(string name) => root.TryGetProperty(name, out var value) ? ControlRequestBody.Uuid(value) :
                throw new ControlRequestException(400, "request_identity_invalid");
            if (!root.TryGetProperty("scriptRevision", out var value) || value.ValueKind != System.Text.Json.JsonValueKind.Number ||
                !value.TryGetInt64(out var revision) || revision < 1)
                throw new ControlRequestException(400, "request_revision_invalid");
            var owner = new DraftBindingIdentity(Id("userId"), Id("scriptId"), revision);
            var result = await service.DiscoverDraftAsync(request.SubjectId, Id("bindingId"), owner, drafts, Id("candidateId"),
                Id("operationId"), context.RequestAborted, () => request.Require(ControlCapability.ConfigurationRead));
            return Results.Json(new { result.Tasks }, NotificationUseCases.Json);
        });
    }
}
