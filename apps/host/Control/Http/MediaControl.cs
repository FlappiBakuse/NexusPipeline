using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Media;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class MediaControl(ControlAuthentication authority, MediaUseCases media, ContractReader contracts)
{
    internal void Map(WebApplication app)
    {
        app.MapPost("/control/v1/media/previews", async (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.MediaGrant); Query(context);
            using var body = await ControlRequestBody.ReadAsync(context);
            body.RequireSchema(contracts, "control-media-grant.schema.json");
            var root = body.Root;
            return Results.Json(await media.OpenAsync(request, ControlRequestBody.Uuid(root.GetProperty("runId")),
                ControlRequestBody.Uuid(root.GetProperty("attemptId")), root.GetProperty("extensionId").GetString()!,
                root.GetProperty("providerId").GetString()!), NotificationUseCases.Json);
        });
        app.MapGet("/control/v1/media/previews/{previewId}/frame", async (HttpContext context, string previewId) =>
        {
            var request = authority.Require(context, ControlCapability.MediaGrant); Query(context);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var image = await media.CaptureAsync(request, ControlQueryValues.Id(previewId), deadline.Token);
            context.Response.Headers["X-Captured-At"] = image.CapturedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            context.Response.ContentType = "image/png"; context.Response.ContentLength = image.SizeBytes;
            context.Response.Headers["X-Content-Sha256"] = Convert.ToHexStringLower(SHA256.HashData(image.Png.Span));
            context.Response.Headers["X-Image-Width"] = image.Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["X-Image-Height"] = image.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await context.Response.Body.WriteAsync(image.Png, context.RequestAborted);
        });
        app.MapPost("/control/v1/media/previews/{previewId}/close", async (HttpContext context, string previewId) =>
        {
            var request = authority.Require(context, ControlCapability.MediaGrant); Query(context);
            if (context.Request.ContentLength is not (null or 0) || context.Request.Headers.TransferEncoding.Count != 0)
                throw new ControlRequestException(400, "request_body_invalid");
            await media.CloseAsync(request, ControlQueryValues.Id(previewId));
            return Results.Json(new { previewId, closed = true });
        });
    }
    private static void Query(HttpContext context)
    { if (context.Request.QueryString.HasValue) throw new ControlRequestException(400, "request_query_invalid"); }
}
