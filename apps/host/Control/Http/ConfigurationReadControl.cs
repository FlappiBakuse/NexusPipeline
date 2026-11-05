using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class ConfigurationReadControl(ControlAuthentication authority, ConfigurationOverviewReader reader)
{
    internal void Map(WebApplication app)
    {
        app.MapGet("/control/v1/configuration/bindings/{bindingId}/resources", (HttpContext context, string bindingId) =>
        {
            authority.Require(context, ControlCapability.ConfigurationRead);
            new ControlQueryValues(context.Request.Query).Validate();
            return Results.Json(reader.Read(ControlQueryValues.Id(bindingId)), NotificationUseCases.Json);
        });
    }
}
