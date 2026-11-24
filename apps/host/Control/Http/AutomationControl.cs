using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Automation;
using NexusPipeline.Host.Control.Configuration;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using System.Runtime.Versioning;
using System.Text.Json;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class AutomationControl(ControlAuthentication authority, AutomationUseCases useCases, AutomationControlCodec codec,
    AccountSummaries summaries, BindingDrafts? drafts = null)
{
    internal void Map(WebApplication app)
    {
        app.MapGet("/control/v1/automation/users/summary", (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.AutomationRead);
            var query = new ControlQueryValues(context.Request.Query); query.Validate();
            return Results.Json(summaries.Read(request), NotificationUseCases.Json);
        });
        app.MapGet("/control/v1/automation/resolve", (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.AutomationRead);
            var query = new ControlQueryValues(context.Request.Query, "kind", "value"); query.Validate();
            return Results.Json(useCases.Resolve(request, query.Text("kind") ?? "", query.Text("value") ?? ""), NotificationUseCases.Json);
        });
        app.MapGet("/control/v1/automation", (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.AutomationRead);
            var query = new ControlQueryValues(context.Request.Query); query.Validate();
            return Results.Json(useCases.Read(request), NotificationUseCases.Json);
        });
        app.MapGet("/control/v1/automation/bindings/{bindingId}/effective", (HttpContext context, string bindingId) =>
        {
            var request = authority.Require(context, ControlCapability.AutomationRead);
            var query = new ControlQueryValues(context.Request.Query); query.Validate();
            return Results.Json(useCases.BindingEffective(request, ControlQueryValues.Id(bindingId)), NotificationUseCases.Json);
        });
        app.MapGet("/control/v1/scheduling", (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.SchedulingRead);
            var query = new ControlQueryValues(context.Request.Query); query.Validate();
            return Results.Json(useCases.Schedules(request), NotificationUseCases.Json);
        });
        app.MapPost("/control/v1/automation/change", async (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.AutomationWrite);
            var query = new ControlQueryValues(context.Request.Query); query.Validate();
            using var body = await ControlRequestBody.ReadAsync(context);
            var command = codec.ReadParsed(body.Root);
            return Results.Json(useCases.Change(request, command), NotificationUseCases.Json);
        });
        app.MapPost("/control/v1/automation/change-drafts", async (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.AutomationWrite);
            request.Require(ControlCapability.ConfigurationEdit);
            new ControlQueryValues(context.Request.Query).Validate();
            using var body = await ControlRequestBody.ReadAsync(context);
            ControlRequestBody.RequireMembers(body.Root, "change", "candidates");
            if (!body.Root.TryGetProperty("change", out var change) || change.ValueKind != JsonValueKind.Object ||
                !body.Root.TryGetProperty("candidates", out var values) || values.ValueKind != JsonValueKind.Array ||
                values.GetArrayLength() is < 1 or > 32)
                throw new ControlRequestException(400, "configuration_draft_selection_invalid");
            var ids = values.EnumerateArray().Select(value => ControlRequestBody.Uuid(value)).ToArray();
            return Results.Json(useCases.Change(request, codec.ReadParsed(change), ids, drafts ??
                throw new ControlRequestException(503, "configuration_draft_unavailable")), NotificationUseCases.Json);
        });
    }
}

[SupportedOSPlatform("windows")]
internal sealed class ScriptSourceControl(ControlAuthentication authority, SourceUseCases sources)
{
    internal void Map(WebApplication app)
    {
        app.MapPost("/control/v1/automation/scripts/root", async (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.AutomationRead);
            new ControlQueryValues(context.Request.Query).Validate();
            using var body = await ControlRequestBody.ReadAsync(context);
            return Results.Json(sources.InspectRoot(request, body.Root, context.RequestAborted), NotificationUseCases.Json);
        });
        app.MapPost("/control/v1/automation/scripts/discover", async (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.ConfigurationRead);
            new ControlQueryValues(context.Request.Query).Validate();
            using var body = await ControlRequestBody.ReadAsync(context);
            var result = await sources.DiscoverAsync(request, body.Root, context.RequestAborted);
            return Results.Json(result, NotificationUseCases.Json);
        });
    }
}
