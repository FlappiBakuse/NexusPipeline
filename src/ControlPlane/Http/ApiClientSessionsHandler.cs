using NexusPipeline.Modules.ClientSessions;

namespace NexusPipeline.ControlPlane.Http;

[ApiRoute("client-sessions")]
internal static class ApiClientSessionsHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body, ClientSessionService sessions)
    {
        if (method == "POST" && seg.Length == 1)
        {
            if (!string.IsNullOrWhiteSpace(body) && body.Trim() != "{}") { await HttpHelper.ErrorAsync(context, "client_session_request_invalid", 400); return; }
            try
            {
                var session = sessions.CreateWeb();
                await HttpHelper.WriteJsonAsync(context, new { token = session.Token, session = Public(session.Context) }, cacheControl: "no-store");
            }
            catch (InvalidOperationException) { await HttpHelper.ErrorAsync(context, "client_session_limit", 429); }
            return;
        }
        if (seg.Length == 2 && seg[1] == "current")
        {
            if (context.ClientSession is not { } client) { await HttpHelper.ErrorAsync(context, "client_session_required", 401); return; }
            if (method == "GET") { await HttpHelper.WriteJsonAsync(context, Public(client), cacheControl: "no-store"); return; }
            if (method == "DELETE") { sessions.Revoke(context.Request.Headers["X-Nxp-Client-Session"]); await HttpHelper.NoContentAsync(context); return; }
        }
        await HttpHelper.NotFoundAsync(context);
    }
    private static object Public(NexusPipeline.Plugin.Abstractions.PluginClientSessionContext client) => new
    { client.HostSessionId, client.ClientSessionId, client.ClientKind, client.NativeBrowserAvailable };
}
