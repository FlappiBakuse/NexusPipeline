using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Automation;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class AutomationExchangeControl(ControlAuthentication authority, ExchangeUseCases exchange, ContractReader contracts)
{
    internal void Map(WebApplication app)
    {
        app.MapPost("/control/v1/automation/import/{action}", async (HttpContext context, string action) =>
        {
            if (action is not ("preview" or "apply")) throw new ControlRequestException(404, "control_not_found");
            var request = authority.Require(context, ControlCapability.AutomationExchange);
            Query(context);
            using var body = await ControlRequestBody.ReadAsync(context);
            body.RequireSchema(contracts, "control-automation-import.schema.json");
            if (body.Root.GetProperty("action").GetString() != action) throw new ControlRequestException(400, "request_action_invalid");
            var result = exchange.Import(request, action, body.Root);
            return action == "apply" ? Results.Json(result, NotificationUseCases.Json, statusCode: 202)
                : Results.Json(result, NotificationUseCases.Json);
        });
        app.MapGet("/control/v1/automation/export", (HttpContext context) =>
        {
            var query = Query(context, "revision", "templateScriptId");
            using var export = exchange.Export(authority.Require(context, ControlCapability.AutomationExchange),
                query.LongInteger("revision", -1), query.Text("templateScriptId") is { } template ? ControlQueryValues.Id(template) : null);
            using var bundle = StrictJson.Parse(export.ExportBundle());
            return Results.Json(new { bundle = bundle.RootElement.Clone(), export.Attachments }, NotificationUseCases.Json);
        });
        app.MapGet("/control/v1/automation/export/{assetId}/original", async (HttpContext context, string assetId) =>
        {
            var request = authority.Require(context, ControlCapability.AutomationExchange);
            var query = Query(context, "revision", "templateScriptId", "sourceSha256");
            using var export = exchange.Export(request, query.LongInteger("revision", -1),
                query.Text("templateScriptId") is { } template ? ControlQueryValues.Id(template) : null);
            var id = ControlQueryValues.Id(assetId);
            var asset = export.Attachments.SingleOrDefault(item => item.Id == id && item.Sha256 == query.Digest("sourceSha256"))
                ?? throw new ControlRequestException(404, "automation_attachment_not_found");
            request.Require(ControlCapability.AutomationExchange);
            context.Response.ContentType = "application/octet-stream"; context.Response.ContentLength = asset.SizeBytes;
            await export.CopyAttachmentToAsync(id, context.Response.Body, context.RequestAborted);
        });
    }
    private static ControlQueryValues Query(HttpContext context, params string[] names)
    { var query = new ControlQueryValues(context.Request.Query, names); query.Validate(1024); return query; }
}
