using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Control;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class CompletionLocalControl(CompletionActionService completion, ControlAuthentication authority)
{
    internal void Map(WebApplication app)
    {
        app.MapGet("/control/v1/completion/pending", (HttpContext context) =>
        {
            authority.Require(context, ControlCapability.StatusRead);
            new ControlQueryValues(context.Request.Query).Validate();
            var pending = completion.PendingOperationId is { } id ? completion.Read(id) : null;
            return Results.Json(new { operationId = pending?.OperationId, action = pending?.Frozen.Action.ToString(),
                phase = pending?.Phase.ToString(), notBeforeUtc = pending?.NotBeforeUtc, serverTimeUtc = DateTimeOffset.UtcNow,
                canCancel = pending is not null && pending.Phase == CompletionEffectPhase.Countdown &&
                    CompletionActionPolicy.CanCancel(pending.Frozen.Action), failureCode = completion.FailureCode });
        });
        app.MapPost("/control/v1/local/completion/{operationId}/cancel", async (HttpContext context, string operationId) =>
        {
            var request = authority.Require(context, ControlCapability.CompletionLocal);
            if (request.Origin != ControlOrigin.Local || !RequestAuthority.IsAllowed(context)) return Results.StatusCode(403);
            new ControlQueryValues(context.Request.Query).Validate();
            using var body = await SecretRequestBody.ReadAsync(context, 0);
            try { await completion.CancelLocalAsync(ControlQueryValues.Id(operationId), context.RequestAborted); }
            catch (InvalidOperationException) { return ControlErrors.Result(409, "completion_not_cancellable"); }
            return Results.Json(new { cancelled = true });
        });
        app.MapPost("/control/v1/local/completion/revoke", async (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.CompletionLocal);
            if (request.Origin != ControlOrigin.Local || !RequestAuthority.IsAllowed(context)) return Results.StatusCode(403);
            var extra = new byte[1];
            if (await context.Request.Body.ReadAsync(extra, context.RequestAborted) != 0) return Results.StatusCode(400);
            return Results.Json(new { revoked = completion.RevokeLocalAuthorization() });
        });
    }
}
