using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Events;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class ControlEvents(ControlAuthentication authority, ControlEventFeed feed)
{
    internal void Map(WebApplication app)
    {
        app.MapGet("/control/v1/events/snapshot", (HttpContext context) =>
        {
            _ = authority.Require(context, ControlCapability.StatusRead);
            Query(context);
            return Results.Json(feed.Snapshot());
        });
        app.MapGet("/control/v1/events", async (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.StatusRead);
            var query = Query(context, "streamId", "afterSequence");
            var stream = ControlQueryValues.Id(query.Text("streamId") ?? "");
            var after = query.LongInteger("afterSequence", -1);
            if (after is < 0 or > 9007199254740991) throw new ControlRequestException(400, "query_invalid");
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-store";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                do
                {
                    request.Require(ControlCapability.StatusRead);
                    var values = feed.Read(stream, after);
                    foreach (var value in values)
                    {
                        await context.Response.WriteAsync(value.Frame, deadline.Token);
                        after = value.Sequence;
                        if (value.Kind == "resync") return;
                    }
                    if (values.Count == 0) await context.Response.WriteAsync(": keepalive\n\n", deadline.Token);
                    await context.Response.Body.FlushAsync(deadline.Token);
                } while (await timer.WaitForNextTickAsync(deadline.Token));
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        });
    }
    private static ControlQueryValues Query(HttpContext context, params string[] names)
    { var query = new ControlQueryValues(context.Request.Query, names); query.Validate(); return query; }
}
