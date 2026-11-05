using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Configuration;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class ProjectControl(ControlAuthentication authority, ProjectUseCases projects, ContractReader contracts)
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
            body.RequireSchema(contracts, "control-project-command.schema.json");
            if (body.Root.GetProperty("action").GetString() != action) throw new ControlRequestException(400, "project_action_mismatch");
            var result = await projects.ExecuteAsync(request, action, body.Root, context.RequestAborted);
            return Results.Json(result, NotificationUseCases.Json);
        });
    }
}
