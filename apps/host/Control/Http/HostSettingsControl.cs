using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Host.Control.Settings;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class HostSettingsControl(ControlAuthentication authority, SettingsUseCases settings, ContractReader contracts)
{
    internal void Map(WebApplication app)
    {
        app.MapGet("/control/v1/settings", (HttpContext context) =>
        {
            NoQuery(context);
            return Results.Json(settings.Read(authority.Require(context, ControlCapability.SettingsRead)), NotificationUseCases.Json);
        });
        app.MapPost("/control/v1/settings", async (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.SettingsWrite); NoQuery(context);
            using var body = await ControlRequestBody.ReadAsync(context);
            body.RequireSchema(contracts, "control-host-settings.schema.json");
            var saved = await settings.SaveAsync(request, body.Root, context.RequestAborted);
            app.Logger.LogInformation("Host settings saved");
            return Results.Json(saved, NotificationUseCases.Json);
        });
    }
    private static void NoQuery(HttpContext context)
    { if (context.Request.QueryString.HasValue) throw new ControlRequestException(400, "request_query_invalid"); }
}
