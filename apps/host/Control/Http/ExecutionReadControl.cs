using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Host.Control.Notifications;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class ExecutionReadControl(ControlAuthentication authority, ExecutionRuntimeConsumer execution, LiveLogReader logs)
{
    internal void Map(WebApplication app)
    {
        app.MapGet("/control/v1/execution/active", (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.StatusRead);
            new ControlQueryValues(context.Request.Query).Validate();
            return Results.Json(new { revision = execution.ProgressRevision, items = execution.ReadActive(request.SubjectId) }, NotificationUseCases.Json);
        });
        const string route = "/control/v1/execution/runs/{runId}/attempts/{attemptId}/logs/{stepId}/{role}/events";
        app.MapGet(route, (Delegate)StreamAsync);
    }

    private async Task StreamAsync(HttpContext context, string runId, string attemptId, string stepId, string role)
    {
        var request = authority.Require(context, ControlCapability.LogsRead);
        new ControlQueryValues(context.Request.Query).Validate();
        if (stepId.Length is < 1 or > 120 || stepId.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('-' or '_' or '.')))
            throw new ControlRequestException(400, "query_invalid");
        var key = new RunLogKey(ControlQueryValues.Id(runId), ControlQueryValues.Id(attemptId), stepId, ControlQueryValues.Role(role));
        using var subscription = logs.Subscribe(key);
        context.Response.ContentType = "text/event-stream";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await SendAsync("tail", subscription.InitialTail);
            var available = subscription.Events.WaitToReadAsync(deadline.Token).AsTask();
            while (!deadline.IsCancellationRequested)
            {
                request.Require(ControlCapability.LogsRead);
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                var keepalive = Task.Delay(TimeSpan.FromSeconds(25), heartbeat.Token);
                if (await Task.WhenAny(available, keepalive) == available)
                {
                    await heartbeat.CancelAsync();
                    if (!await available) return;
                    while (subscription.Events.TryRead(out var frame)) await SendAsync("frame", frame);
                    available = subscription.Events.WaitToReadAsync(deadline.Token).AsTask();
                }
                else
                {
                    await context.Response.WriteAsync(": keepalive\n\n", deadline.Token);
                    await context.Response.Body.FlushAsync(deadline.Token);
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        catch (IOException) when (subscription.FailureCode is not null)
        { await SendAsync("resync", new { reasonCode = subscription.FailureCode }); }

        async Task SendAsync(string kind, object value)
        {
            request.Require(ControlCapability.LogsRead);
            var message = JsonSerializer.Serialize(new { kind, key, data = value }, NotificationUseCases.Json);
            await context.Response.WriteAsync("data: " + message + "\n\n", deadline.Token);
            await context.Response.Body.FlushAsync(deadline.Token);
        }
    }
}
