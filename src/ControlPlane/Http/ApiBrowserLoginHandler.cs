using System.Text.Json.Nodes;
using NexusPipeline.Modules.Plugins.Runtime;

namespace NexusPipeline.ControlPlane.Http;

[ApiRoute("browser-login")]
internal static class ApiBrowserLoginHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body, PluginManager plugins)
    {
        if (context.ClientSession is not { } client) { await HttpHelper.ErrorAsync(context, "client_session_required", 401); return; }
        try
        {
            if (method == "GET" && seg.Length == 2)
            { await HttpHelper.WriteJsonAsync(context, plugins.BrowserLogin.Status(client, seg[1]), cacheControl: "no-store"); return; }
            if (method == "DELETE" && seg.Length == 2)
            { await plugins.BrowserLogin.CancelAsync(client, seg[1]); await HttpHelper.NoContentAsync(context); return; }
            await HttpHelper.NotFoundAsync(context);
        }
        catch (InvalidOperationException) { await HttpHelper.ErrorAsync(context, "browser_operation_not_found", 404); }
    }
}
