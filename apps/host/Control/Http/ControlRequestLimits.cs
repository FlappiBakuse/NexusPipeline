namespace NexusPipeline.Host.Control.Http;

internal sealed class ControlRequestLimits
{
    private readonly SemaphoreSlim _requests = new(32, 32);
    internal async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!await _requests.WaitAsync(0, context.RequestAborted))
        {
            context.Response.Headers.CacheControl = "no-store";
            await ControlErrors.Result(503, "control_busy").ExecuteAsync(context); return;
        }
        try { await next(context); }
        finally { _requests.Release(); }
    }
}
