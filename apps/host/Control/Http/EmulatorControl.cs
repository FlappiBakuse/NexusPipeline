using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Serialization.Protocol;
using NexusPipeline.Platform.Storage.Emulation;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class EmulatorControl(ControlAuthentication authority, EmulatorTargetService emulators, ContractReader contracts)
{
    internal void Map(WebApplication app)
    {
        app.MapPost("/control/v1/automation/emulators/discover", async (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.AutomationRead);
            var query = new ControlQueryValues(context.Request.Query); query.Validate();
            using var body = await ControlRequestBody.ReadAsync(context);
            body.RequireSchema(contracts, "control-emulator-discover.schema.json");
            var value = System.Text.Json.JsonSerializer.Deserialize<ControlEmulatorDiscover>(body.Root.GetRawText())
                ?? throw new ControlRequestException(400, "emulator_request_invalid");
            var result = await emulators.DiscoverAsync(Guid.Parse(value.ScriptId), value.ExpectedCatalogRevision,
                value.RootIds.Select(Guid.Parse).ToArray(), () => request.Require(ControlCapability.AutomationRead), context.RequestAborted);
            request.Require(ControlCapability.AutomationRead);
            return Results.Json(result, NotificationUseCases.Json);
        });
    }
}
