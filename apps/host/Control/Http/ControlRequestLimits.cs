namespace NexusPipeline.Host.Control.Http;

internal sealed class ControlRequestLimits
{
    private enum Lane { Subscription, Stream }
    private sealed record Admission(Lane Lane);

    internal static object EventSubscription { get; } = new Admission(Lane.Subscription);
    internal static object ResponseStream { get; } = new Admission(Lane.Stream);

    // Keep the existing total budget at 32 and reserve separate capacity for short controls.
    private readonly SemaphoreSlim _controls = new(16, 16);
    private readonly SemaphoreSlim _subscriptions = new(8, 8);
    private readonly SemaphoreSlim _streams = new(8, 8);

    internal async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var budget = context.GetEndpoint()?.Metadata.GetMetadata<Admission>()?.Lane switch
        {
            Lane.Subscription => _subscriptions,
            Lane.Stream => _streams,
            _ => _controls
        };
        if (!await budget.WaitAsync(0, context.RequestAborted))
        {
            context.Response.Headers.CacheControl = "no-store";
            await ControlErrors.Result(503, "control_busy").ExecuteAsync(context); return;
        }
        try { await next(context); }
        finally { budget.Release(); }
    }
}
