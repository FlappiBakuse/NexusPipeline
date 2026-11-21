using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class NativeConfigurationControl(ControlAuthentication authority, NativeEditorService service,
    BindingEditorService bindings, ConfigurationEditController edits, ContractReader contracts)
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
        app.MapPost("/control/v1/configuration/editor/open-binding", async (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.NativeConfigurationOpen); Query(context);
            using var body = await ControlRequestBody.ReadAsync(context, 4096);
            var root = body.Root;
            ControlRequestBody.RequireMembers(root, "sessionId", "bindingId", "expectedBindingRevision", "expectedScriptRevision", "candidateId", "draft", "draftCandidateId");
            var scriptRevision = Revision(root, "expectedScriptRevision");
            var draft = ReadDraft(root, scriptRevision);
            var selection = new BindingEditorSelection(Identity(root, "sessionId"), Identity(root, "bindingId"),
                draft is null ? Revision(root, "expectedBindingRevision") : DraftRevision(root), scriptRevision, Candidate(root))
            {
                Draft = draft,
                DraftCandidateId = root.TryGetProperty("draftCandidateId", out var prior) ? ControlRequestBody.Uuid(prior) : null
            };
            var opened = await bindings.OpenAsync(request.SubjectId, selection,
                () => request.Require(ControlCapability.NativeConfigurationOpen), context.RequestAborted);
            return Results.Json(new { session = opened.Session is { } status ? Public(status) : null, opened.Options, opened.Issue, opened.CanResume },
                NotificationUseCases.Json);
        });
        app.MapPost("/control/v1/configuration/editor/{sessionId}/save-binding", async (HttpContext context, string sessionId) =>
        {
            var request = authority.Require(context, ControlCapability.NativeConfigurationOpen); Query(context);
            using var body = await ControlRequestBody.ReadAsync(context, 4096);
            ControlRequestBody.RequireMembers(body.Root, "operationId", "expectedSessionRevision", "candidateId");
            var id = ControlQueryValues.Id(sessionId);
            var result = await edits.SaveBindingAsync(Identity(body.Root, "operationId"), request.SubjectId, id,
                Revision(body.Root, "expectedSessionRevision"), Candidate(body.Root), DateTimeOffset.UtcNow);
            return Results.Json(new { session = Public(edits.Read(id, request.SubjectId)!), result.Inspection, result.CanResume, result.DraftCandidate },
                NotificationUseCases.Json);
        });
        app.MapPost("/control/v1/configuration/editor/drafts/{draftId}/discard", (HttpContext context, string draftId) =>
        {
            var request = authority.Require(context, ControlCapability.ConfigurationEdit); Query(context);
            edits.Drafts.Discard(ControlQueryValues.Id(draftId), request.SubjectId);
            return Results.Json(new { discarded = true }, NotificationUseCases.Json);
        });
        app.MapPost("/control/v1/configuration/editor/{sessionId}/resume-binding", async (HttpContext context, string sessionId) =>
        {
            var request = authority.Require(context, ControlCapability.NativeConfigurationOpen); Query(context);
            using var body = await ControlRequestBody.ReadAsync(context, 4096);
            ControlRequestBody.RequireMembers(body.Root, "expectedSessionRevision", "nextSessionId");
            var status = await edits.ResumeBindingAsync(request.SubjectId, ControlQueryValues.Id(sessionId),
                Revision(body.Root, "expectedSessionRevision"), Identity(body.Root, "nextSessionId"));
            return Results.Json(Public(status), NotificationUseCases.Json);
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
    private static Guid Identity(JsonElement value, string name) =>
        value.TryGetProperty(name, out var member) ? ControlRequestBody.Uuid(member) :
            throw new ControlRequestException(400, "request_identity_invalid");
    private static long Revision(JsonElement value, string name) => value.TryGetProperty(name, out var member) &&
        member.ValueKind == JsonValueKind.Number && member.TryGetInt64(out var revision) && revision is >= 1 and <= 9007199254740991
        ? revision : throw new ControlRequestException(400, "request_revision_invalid");
    private static long DraftRevision(JsonElement value) =>
        value.TryGetProperty("expectedBindingRevision", out var revision) && revision.ValueKind == JsonValueKind.Number &&
        revision.TryGetInt64(out var number) && number == 0 ? number : throw new ControlRequestException(400, "request_revision_invalid");
    private static DraftBindingIdentity? ReadDraft(JsonElement root, long scriptRevision)
    {
        if (!root.TryGetProperty("draft", out var draft)) return null;
        ControlRequestBody.RequireMembers(draft, "userId", "scriptId");
        return new(Identity(draft, "userId"), Identity(draft, "scriptId"), scriptRevision);
    }
    private static string? Candidate(JsonElement value)
    {
        if (!value.TryGetProperty("candidateId", out var member)) return null;
        return member.ValueKind == JsonValueKind.String && member.GetString() is { Length: >= 1 and <= 1024 } id &&
            !id.Any(char.IsControl) ? id : throw new ControlRequestException(400, "request_shape_invalid");
    }
    private static void Query(HttpContext context)
    { if (context.Request.QueryString.HasValue) throw new ControlRequestException(400, "request_query_invalid"); }
}
