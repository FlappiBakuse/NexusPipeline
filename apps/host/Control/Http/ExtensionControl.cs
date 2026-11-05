using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Host.Control.Extensions;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class ExtensionControl(ControlAuthentication authority, ExtensionUseCases extensions, ContractReader contracts)
{
    internal void Map(WebApplication app)
    {
        app.MapGet("/control/v1/extensions", (HttpContext context) =>
        { Query(context); return Results.Json(extensions.Read(authority.Require(context, ControlCapability.ExtensionsRead)), NotificationUseCases.Json); });
        app.MapGet("/control/v1/extensions/catalog/{channel}", (HttpContext context, string channel) =>
        {
            Query(context);
            var value = extensions.Catalog(authority.Require(context, ControlCapability.ExtensionsRead), Channel(channel));
            return value is null ? (IResult)Results.NoContent() : Results.Json(value, NotificationUseCases.Json);
        });
        app.MapGet("/control/v1/extensions/catalog/{channel}/entries/{extensionId}/details",
            async (HttpContext context, string channel, string extensionId) =>
        {
            var request = authority.Require(context, ControlCapability.ExtensionsRead);
            var query = new ControlQueryValues(context.Request.Query, "generationId", "sequence", "sha256");
            query.Validate();
            var sequence = query.LongInteger("sequence", -1);
            if (sequence < 1) throw new ControlRequestException(400, "extension_catalog_identity_invalid");
            var identity = new ExtensionCatalogIdentity(Channel(channel),
                ControlQueryValues.Id(query.Text("generationId") ?? ""), sequence, query.Digest("sha256"));
            var value = await extensions.CatalogDetailsAsync(request, identity, extensionId, context.RequestAborted);
            return value is null ? Results.NotFound() : Results.Json(value, NotificationUseCases.Json);
        });
        app.MapPost("/control/v1/extensions/catalog/{channel}/refresh", async (HttpContext context, string channel) =>
        {
            Query(context); Empty(context);
            return Results.Json(await extensions.RefreshAsync(authority.Require(context, ControlCapability.ExtensionsWrite), Channel(channel)),
                NotificationUseCases.Json);
        });
        app.MapGet("/control/v1/extensions/operations/{operationId}", (HttpContext context, string operationId) =>
        {
            Query(context); var value = extensions.Operation(authority.Require(context, ControlCapability.OperationsRead), ControlQueryValues.Id(operationId));
            return value is null ? Results.NotFound() : Results.Json(value, NotificationUseCases.Json);
        });
        app.MapGet("/control/v1/extensions/operations", (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.OperationsRead);
            var query = new ControlQueryValues(context.Request.Query, "offset", "limit"); query.Validate();
            var offset = int.TryParse(query.Text("offset") ?? "0", out var parsedOffset) ? parsedOffset : -1;
            var limit = int.TryParse(query.Text("limit") ?? "20", out var parsedLimit) ? parsedLimit : -1;
            if (offset is < 0 or > 10000 || limit is < 1 or > 50)
                throw new ControlRequestException(400, "extension_operation_page_invalid");
            return Results.Json(extensions.OperationHistory(request, offset, limit), NotificationUseCases.Json);
        });
        app.MapPost("/control/v1/extensions/apply", async (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.ExtensionsWrite); Query(context);
            using var body = await ControlRequestBody.ReadAsync(context);
            body.RequireSchema(contracts, "control-extension-apply.schema.json");
            return Results.Json(await extensions.ApplyAsync(request, body.Root), NotificationUseCases.Json, statusCode: 202);
        });
        app.MapPost("/control/v1/extensions/reconcile", async (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.ExtensionsWrite); Query(context);
            using var body = await ControlRequestBody.ReadAsync(context);
            body.RequireSchema(contracts, "control-extension-reconcile.schema.json");
            return Results.Json(await extensions.ReconcileAsync(request, body.Root), NotificationUseCases.Json);
        });
    }
    private static ExtensionStoreChannel Channel(string value) => value switch
    { "stable" => ExtensionStoreChannel.Stable, "develop" => ExtensionStoreChannel.Develop, _ => throw new ControlRequestException(400, "extension_channel_invalid") };
    private static void Query(HttpContext context)
    { if (context.Request.QueryString.HasValue) throw new ControlRequestException(400, "request_query_invalid"); }
    private static void Empty(HttpContext context)
    { if (context.Request.ContentLength is not (null or 0) || context.Request.Headers.TransferEncoding.Count != 0) throw new ControlRequestException(400, "request_body_invalid"); }
}
