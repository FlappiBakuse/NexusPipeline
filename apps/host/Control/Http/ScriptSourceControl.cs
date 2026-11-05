using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Configuration;
using NexusPipeline.Host.Control.Notifications;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class ScriptSourceControl(ControlAuthentication authority, SourceUseCases sources)
{
    internal void Map(WebApplication app)
    {
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
