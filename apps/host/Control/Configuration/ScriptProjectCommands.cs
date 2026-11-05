using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Host.Control.Configuration;

[SupportedOSPlatform("windows")]
internal sealed class ScriptProjectCommands(ScriptProjectService scripts, ProjectPreviewService previews)
{
    internal async ValueTask<object> ExecuteAsync(AuthenticatedControlRequest request, string action, JsonElement body,
        string subject, Action current, CancellationToken cancellation, CancellationToken ownerLifetime)
    {
        var script = ControlRequestBody.Uuid(body.GetProperty("scriptId"));
        if (action == "context") return scripts.ReadContext(script, current);
        var revision = body.GetProperty("expectedScriptRevision").GetInt64();
        var extension = body.GetProperty("extensionId").GetString()!;
        if (action == "discover") return await scripts.DiscoverAsync(script, revision, extension, body.GetProperty("selection"), current, cancellation);
        if (action == "preview")
        {
            var selection = body.TryGetProperty("selection", out var draft) ? draft :
                scripts.ReadContext(script, current).Selection ?? throw new IOException("project_selection_required");
            return await previews.PrepareScriptAsync(scripts, subject, script, revision, extension, selection, current, cancellation, ownerLifetime);
        }
        var operation = ControlRequestBody.Uuid(body.GetProperty("operationId"));
        var expected = body.GetProperty("expectedSelectionRevision").GetInt64();
        var provider = body.TryGetProperty("providerId", out var named) ? named.GetString()! : scripts.ReadContext(script, current).ProviderId;
        if (action == "result")
        {
            var result = scripts.ReadAccepted(operation, request.SubjectId, script, revision, expected, extension, provider, body.GetProperty("selection"), current);
            return new { OperationId = operation, Found = result is not null, Pending = result is null,
                Outcome = result is null ? "pending" : "committed", Revision = result?.Revision };
        }
        if (action != "select") throw new ControlRequestException(404, "control_not_found");
        var saved = await scripts.SaveAsync(operation, request.SubjectId, script, revision, expected, extension,
            body.GetProperty("selection"), current, cancellation, provider);
        return new { saved.OperationId, saved.Revision, saved.IsNew };
    }
}
