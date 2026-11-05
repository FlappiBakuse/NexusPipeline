using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Configuration;
using NexusPipeline.Host.Control.Notifications;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class ConfigurationImportControl(ControlAuthentication authority, ImportUseCases imports)
{
    internal void Map(WebApplication app)
    {
        app.MapPost("/control/v1/configuration/import", async (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.ConfigurationEdit);
            new ControlQueryValues(context.Request.Query).Validate();
            using var body = await ControlRequestBody.ReadAsync(context, 16384);
            return Results.Json(imports.Execute(request, body.Root, context.RequestAborted), NotificationUseCases.Json);
        });
    }
}
