using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Configuration;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class ConfigurationValueControl(ControlAuthentication authority, ValueUseCases values, ContractReader contracts)
{
    internal void Map(WebApplication app)
    {
        app.MapGet("/control/v1/configuration/generic/{bindingId}", (HttpContext context, string bindingId) =>
        {
            var request = authority.Require(context, ControlCapability.ConfigurationRead);
            Query(context);
            return Results.Json(values.Read(request, ControlQueryValues.Id(bindingId)), NotificationUseCases.Json);
        });
        app.MapGet("/control/v1/configuration/generic/{bindingId}/{resourceId}/files",
            (HttpContext context, string bindingId, string resourceId) =>
        {
            var request = authority.Require(context, ControlCapability.ConfigurationRead);
            var query = Query(context, "revision", "afterPosition", "limit");
            return Results.Json(values.Files(request, ControlQueryValues.Id(bindingId), resourceId,
                query.LongInteger("revision", 0), query.Integer("afterPosition", -1), query.Integer("limit", 50)),
                NotificationUseCases.Json);
        });
        app.MapGet("/control/v1/configuration/generic/{bindingId}/{resourceId}/original",
            (HttpContext context, string bindingId, string resourceId) =>
        {
            var request = authority.Require(context, ControlCapability.ConfigurationRead);
            var query = Query(context, "revision", "relativePath", "sourceSha256");
            var revision = query.LongInteger("revision", 0);
            if (revision is < 1 or > 9007199254740991) throw new ControlRequestException(400, "query_invalid");
            return Results.Stream(values.OpenOriginal(request, ControlQueryValues.Id(bindingId), resourceId, revision,
                query.Text("relativePath") ?? "", query.Digest("sourceSha256")),
                "application/octet-stream", "configuration.bin", enableRangeProcessing: false);
        });
        app.MapGet("/control/v1/configuration/generic/{bindingId}/{resourceId}/document",
            (HttpContext context, string bindingId, string resourceId) =>
        {
            var request = authority.Require(context, ControlCapability.ConfigurationRead);
            var query = Query(context, "revision", "relativePath", "sourceSha256", "syntax");
            var revision = query.LongInteger("revision", 0);
            if (revision is < 1 or > 9007199254740991) throw new ControlRequestException(400, "query_invalid");
            return Results.Json(values.Document(request, ControlQueryValues.Id(bindingId), resourceId, revision,
                query.Text("relativePath") ?? "", query.Digest("sourceSha256"), query.Text("syntax") ?? ""),
                NotificationUseCases.Json);
        });
        app.MapPost("/control/v1/configuration/generic/{bindingId}/value/{action}",
            async (HttpContext context, string bindingId, string action) =>
        {
            if (action is not ("preview" or "apply")) throw new ControlRequestException(404, "control_not_found");
            var request = authority.Require(context, ControlCapability.ConfigurationEdit); Query(context);
            using var body = await ControlRequestBody.ReadAsync(context);
            body.RequireSchema(contracts, "control-configuration-value.schema.json");
            return Results.Json(values.Execute(request, ControlQueryValues.Id(bindingId), action, body.Root), NotificationUseCases.Json);
        });
    }
    private static ControlQueryValues Query(HttpContext context, params string[] names)
    { var query = new ControlQueryValues(context.Request.Query, names); query.Validate(1024); return query; }
}
