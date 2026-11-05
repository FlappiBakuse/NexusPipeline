using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Settings;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class InterfaceControl(ControlAuthentication authority, InterfacePreferences preferences, ContractReader contracts)
{
    internal void Map(WebApplication app)
    {
        app.MapGet("/control/v1/settings/interface", (HttpContext context) =>
        {
            authority.Require(context, ControlCapability.SettingsRead); NoQuery(context);
            return Results.Json(preferences.Read(), NotificationUseCases.Json);
        });
        app.MapPost("/control/v1/settings/interface/change", async (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.SettingsWrite); NoQuery(context);
            using var body = await ControlRequestBody.ReadAsync(context, 8192);
            body.RequireSchema(contracts, "control-interface-change.schema.json");
            var root = body.Root; var values = root.GetProperty("values");
            var operation = ControlRequestBody.Uuid(root.GetProperty("operationId"));
            try
            {
                var saved = preferences.Save(operation, request.SubjectId, root.GetProperty("expectedRevision").GetInt64(),
                    new(values.GetProperty("theme").GetString()!,
                        Strings("dashboardCards"), Strings("pinnedSettings"), Strings("collapsedSettings"),
                        values.GetProperty("locale").GetString()!));
                return Results.Json(saved, NotificationUseCases.Json);
            }
            catch (InterfaceConflictException) { return ControlErrors.Result(409, "interface_revision_conflict", operation); }
            string[] Strings(string name) => values.GetProperty(name).EnumerateArray().Select(value => value.GetString()!).ToArray();
        });
    }
    private static void NoQuery(HttpContext context)
    { if (context.Request.QueryString.HasValue) throw new ControlRequestException(400, "request_query_invalid"); }
}
