using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Configuration;
using NexusPipeline.Host.Control.Notifications;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class ProjectControl(ControlAuthentication authority, ProjectUseCases projects)
{
    internal void Map(WebApplication app)
    {
        app.MapPost("/control/v1/projects/{action}", async (HttpContext context, string action) =>
        {
            if (action is not ("context" or "discover" or "select" or "result" or "preview" or "page" or "release"))
                throw new ControlRequestException(404, "control_not_found");
            var request = authority.Require(context, action == "select" ? ControlCapability.ConfigurationEdit : ControlCapability.ConfigurationRead);
            new ControlQueryValues(context.Request.Query).Validate();
            using var body = await ControlRequestBody.ReadAsync(context);
            var command = projects.Validate(action, body.Root);
            var result = await projects.ExecuteAsync(request, command, context.RequestAborted);
            return Results.Json(result, NotificationUseCases.Json);
        });
    }
}
