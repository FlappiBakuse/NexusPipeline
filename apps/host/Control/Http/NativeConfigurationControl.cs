using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class NativeConfigurationControl(ControlAuthentication authority, NativeEditorService service,
    ConfigurationEditController edits, ContractReader contracts)
{
    internal void Map(WebApplication app)
    {
        app.MapGet("/control/v1/configuration/editor/{sessionId}", (HttpContext context, string sessionId) =>
        {
            var request = authority.Require(context, ControlCapability.NativeConfigurationOpen); Query(context);
            var value = edits.Read(ControlQueryValues.Id(sessionId), request.SubjectId);
            return value is null ? Results.NotFound() : Results.Json(Public(value), NotificationUseCases.Json);
        });
        app.MapPost("/control/v1/configuration/editor/begin", async (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.NativeConfigurationOpen); Query(context);
            using var body = await ControlRequestBody.ReadAsync(context);
            body.RequireSchema(contracts, "control-native-editor.schema.json");
            var root = body.Root;
            var selection = new GenericEditorSelection(ControlRequestBody.Uuid(root.GetProperty("sessionId")),
                ControlRequestBody.Uuid(root.GetProperty("bindingId")), root.GetProperty("expectedBindingRevision").GetInt64(),
                root.GetProperty("expectedScriptRevision").GetInt64(), ControlRequestBody.Uuid(root.GetProperty("editorRootId")),
                root.GetProperty("expectedEditorRootRevision").GetInt64(), root.GetProperty("editorRelativePath").GetString()!,
                root.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()!).ToImmutableArray(),
                root.GetProperty("resources").EnumerateArray().Select(item => new GenericEditorResource(
                    item.GetProperty("resourceId").GetString()!, item.GetProperty("expectedRevision").GetInt64())).ToImmutableArray());
            request.Require(ControlCapability.NativeConfigurationOpen);
            return Results.Json(Public(await service.BeginAsync(request.SubjectId, selection, DateTimeOffset.UtcNow)), NotificationUseCases.Json);
        });
        app.MapPost("/control/v1/configuration/editor/{sessionId}/{action}", async (HttpContext context, string sessionId, string action) =>
        {
            if (action is not ("save" or "cancel")) throw new ControlRequestException(404, "control_not_found");
            var request = authority.Require(context, ControlCapability.NativeConfigurationOpen); Query(context);
            using var body = await ControlRequestBody.ReadAsync(context, 4096);
            body.RequireSchema(contracts, "control-native-editor-finish.schema.json");
            var operation = ControlRequestBody.Uuid(body.Root.GetProperty("operationId"));
            var revision = body.Root.GetProperty("expectedSessionRevision").GetInt64();
            var id = ControlQueryValues.Id(sessionId);
            request.Require(ControlCapability.NativeConfigurationOpen);
            if (action == "cancel")
                return Results.Json(Public(await edits.CancelAsync(operation, request.SubjectId, id, revision, DateTimeOffset.UtcNow)), NotificationUseCases.Json);
            var saved = await edits.SaveAsync(operation, request.SubjectId, id, revision, DateTimeOffset.UtcNow);
            return Results.Json(new { saved.OperationId, saved.IsNew, session = Public(edits.Read(id, request.SubjectId)!) }, NotificationUseCases.Json);
        });
    }
    private static object Public(ConfigurationEditStatus value) => new
    {
        value.SessionId, value.Revision, value.Phase, value.ExpectedBindingRevision, value.CompletionOperationId, value.IsNew,
        resources = value.Resources.Select(item => new { item.Address, item.ExpectedRevision }).ToArray()
    };
    private static void Query(HttpContext context)
    { if (context.Request.QueryString.HasValue) throw new ControlRequestException(400, "request_query_invalid"); }
}
