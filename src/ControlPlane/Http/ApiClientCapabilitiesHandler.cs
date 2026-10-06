namespace NexusPipeline.ControlPlane.Http;

[ApiRoute("client-capabilities")]
internal static class ApiClientCapabilitiesHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg)
    {
        if (method != "GET" || seg.Length != 1) { await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false); return; }
        context.Response.Headers["Cache-Control"] = "no-store";
        await HttpHelper.WriteJsonAsync(context, RequestAccessPolicy.Capabilities(context.Request), cacheControl: "no-store").ConfigureAwait(false);
    }
}
