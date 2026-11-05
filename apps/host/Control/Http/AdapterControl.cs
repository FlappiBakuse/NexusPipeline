using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Configuration;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class AdapterControl(ControlAuthentication authority, AdapterUseCases adapters, ContractReader contracts)
{
    internal void Map(WebApplication app)
    {
        app.MapPost("/control/v1/adapters/{action}", async (HttpContext context, string action) =>
        {
            if (action is not ("discover" or "propose" or "previewRepair" or "applyRepair"))
                throw new ControlRequestException(404, "control_not_found");
            var request = authority.Require(context, action is "previewRepair" or "applyRepair" ?
                ControlCapability.ConfigurationRepair : ControlCapability.ConfigurationRead);
            new ControlQueryValues(context.Request.Query).Validate();
            using var body = await ControlRequestBody.ReadAsync(context);
            body.RequireSchema(contracts, "control-adapter-command.schema.json");
            if (body.Root.GetProperty("action").GetString() != action)
                throw new ControlRequestException(400, "adapter_action_mismatch");
            var result = await adapters.ExecuteAsync(request, action, body.Root);
            return Results.Json(result, NotificationUseCases.Json);
        });
    }
}
