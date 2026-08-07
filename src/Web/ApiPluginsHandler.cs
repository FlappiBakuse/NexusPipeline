using System.Net;

namespace NexusPipeline.Web;

internal static class ApiPluginsHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg)
    {
        if (method != "POST" || seg.Length != 3)
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        string name = seg[1];
        bool enabled = seg[2].ToLowerInvariant() == "enable";
        RuntimeContext.Instance.Plugins.SetEnabled(name, enabled, Audit.Web);
        await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
    }
}
