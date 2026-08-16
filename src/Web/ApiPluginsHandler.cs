using System.Net;
using NexusPipeline.Services;

namespace NexusPipeline.Web;

[ApiRoute("plugins")]
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
        // v0.7.4（KN-40）：显式校验 enable/disable，其余字符串 400（此前任意字符串都按 disable 处理）。
        string verb = seg[2].ToLowerInvariant();
        if (verb is not ("enable" or "disable"))
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "操作无效（应为 enable 或 disable）" }, 400).ConfigureAwait(false);
            return;
        }
        bool enabled = verb == "enable";
        RuntimeContext.Instance.Plugins.SetEnabled(name, enabled, Audit.Web);
        await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
    }
}
