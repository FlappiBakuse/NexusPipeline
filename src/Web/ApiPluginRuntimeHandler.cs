using System.Net;
using NexusPipeline.Plugins;

namespace NexusPipeline.Web;

/// <summary>向管理页面发布已启用、运行态有效且兼容的插件前端模块清单。</summary>
[ApiRoute("plugin-runtime")]
internal static class ApiPluginRuntimeHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body)
    {
        if (method != "GET" || seg.Length != 2 || !seg[1].Equals("frontend", StringComparison.OrdinalIgnoreCase))
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }

        await HttpHelper.WriteJsonAsync(
            context,
            RuntimeContext.Instance.Plugins.FrontendDescriptors.Select(descriptor => new
            {
                name = descriptor.Name,
                displayName = descriptor.DisplayName,
                version = descriptor.Version,
                frontendApiVersion = descriptor.FrontendApiVersion,
                entryUrl = descriptor.EntryUrl,
                styleUrls = descriptor.StyleUrls,
            }).ToArray()).ConfigureAwait(false);
    }
}
