using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Maintenance;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class HostUpdateControl(ControlAuthentication authority, HostUpdateService updates, ContractReader contracts)
{
    internal void Map(WebApplication app)
    {
        app.MapGet("/control/v1/maintenance/feed/{channel}", (HttpContext context, string channel) =>
        {
            _ = authority.Require(context, ControlCapability.MaintenanceRead); Query(context);
            var feed = updates.ReadCached(Channel(channel));
            // An empty cache still needs a JSON body for the strict control client.
            return feed is null ? (IResult)Results.Text("null", "application/json")
                : Results.Json(feed, NotificationUseCases.Json);
        });
        app.MapPost("/control/v1/maintenance/feed/{channel}/refresh", async (HttpContext context, string channel) =>
        {
            var request = authority.Require(context, ControlCapability.MaintenanceWrite); Query(context); Empty(context);
            return Results.Json(await updates.RefreshAsync(request.SubjectId, Channel(channel), context.RequestAborted),
                NotificationUseCases.Json);
        });
        app.MapPost("/control/v1/maintenance/prepare", async (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.MaintenanceWrite); Query(context);
            using var body = await ControlRequestBody.ReadAsync(context);
            body.RequireSchema(contracts, "control-maintenance-prepare.schema.json");
            return Results.Json(await updates.PrepareAsync(Parse(body.Root, request.SubjectId), context.RequestAborted),
                NotificationUseCases.Json, statusCode: 202);
        });
    }

    private static HostUpdateRequest Parse(JsonElement value, string subject)
    {
        var feed = value.GetProperty("feed"); var window = value.GetProperty("window");
        return new(ControlRequestBody.Uuid(value.GetProperty("operationId")), subject,
            new(Channel(feed.GetProperty("channel").GetString()!), feed.GetProperty("sequence").GetInt64(), feed.GetProperty("sha256").GetString()!),
            value.GetProperty("hostVersion").GetString()!, value.GetProperty("timing").GetString() switch
            { "now" => HostUpdateTiming.Now, "nextStartup" => HostUpdateTiming.NextStartup, "whenIdle" => HostUpdateTiming.WhenIdle, _ => throw Invalid() },
            new(window.GetProperty("mode").GetString() switch
            { "desktop" => UpdateWindowMode.Desktop, "browser" => UpdateWindowMode.Browser,
                "headless" => UpdateWindowMode.Headless, _ => throw Invalid() },
                window.GetProperty("visible").GetBoolean(), window.GetProperty("route").GetString()!));
    }

    private static HostUpdateChannel Channel(string value) => value switch
    { "stable" => HostUpdateChannel.Stable, "prerelease" => HostUpdateChannel.Prerelease, _ => throw Invalid() };
    private static void Query(HttpContext context)
    { if (context.Request.QueryString.HasValue) throw new ControlRequestException(400, "request_query_invalid"); }
    private static void Empty(HttpContext context)
    { if (context.Request.ContentLength is not (null or 0) || context.Request.Headers.TransferEncoding.Count != 0) throw new ControlRequestException(400, "request_body_invalid"); }
    private static ControlRequestException Invalid() => new(400, "maintenance_request_invalid");
}
