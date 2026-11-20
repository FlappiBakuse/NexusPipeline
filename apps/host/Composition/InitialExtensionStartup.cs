using NexusPipeline.Host.Control.Http;

namespace NexusPipeline.Host.Composition;

internal sealed class InitialExtensionStartup(bool pending)
{
    private int _pending = pending ? 1 : 0;
    internal bool Pending => Volatile.Read(ref _pending) != 0;

    internal void Complete() => Volatile.Write(ref _pending, 0);

    internal Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        if (!Pending || HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) ||
            HttpMethods.IsOptions(request.Method) || IsFrontendSession(request)) return next(context);
        return ControlErrors.Result(503, "initial_extensions_pending").ExecuteAsync(context);
    }

    private static bool IsFrontendSession(HttpRequest request) => HttpMethods.IsPost(request.Method) &&
        (request.Path.Equals("/control/v1/extensions/clients") || request.Path.Equals("/control/v1/extensions/clients/retire") ||
         request.Path.Equals("/control/v1/desktop/frontend/bind") || request.Path.Equals("/control/v1/desktop/frontend/retire"));
}
