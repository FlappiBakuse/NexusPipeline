using System.Net;
using NexusPipeline.Plugins;
using NexusPipeline.Services;

namespace NexusPipeline.Web;

[ApiRoute("plugins")]
internal static class ApiPluginsHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg)
    {
        if (method == "GET" && seg.Length == 1)
        {
            PluginManager manager = RuntimeContext.Instance.Plugins;
            await HttpHelper.WriteJsonAsync(context, manager.PluginSummaries.Select(plugin => new
            {
                plugin.Name,
                plugin.DisplayName,
                gameName = plugin.GameName,
                plugin.Description,
                plugin.Version,
                kind = plugin.Kind,
                apiVersion = plugin.ApiVersion,
                capabilities = plugin.Capabilities,
                configuredEnabled = manager.IsConfiguredEnabled(plugin.Name),
                runtimeEnabled = manager.IsEnabled(plugin.Name),
                state = manager.GetRuntimeState(plugin.Name),
                error = manager.GetRuntimeError(plugin.Name),
                restartRequired = manager.IsConfiguredEnabled(plugin.Name)
                    != manager.IsEnabled(plugin.Name),
            })).ConfigureAwait(false);
            return;
        }
        if (method != "POST" || seg.Length != 3)
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        string name = seg[1];
        // 显式校验 enable/disable，其余字符串 400（此前任意字符串都按 disable 处理）。
        string verb = seg[2].ToLowerInvariant();
        if (verb is not ("enable" or "disable"))
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "操作无效（应为 enable 或 disable）" }, 400).ConfigureAwait(false);
            return;
        }
        bool enabled = verb == "enable";
        PluginManager plugins = RuntimeContext.Instance.Plugins;
        if (!plugins.SetEnabled(name, enabled, Audit.Web))
        {
            await HttpHelper.WriteJsonAsync(context, new { ok = false, error = $"插件不存在：{name}" }, 404).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new
        {
            ok = true,
            configuredEnabled = plugins.IsConfiguredEnabled(name),
            runtimeEnabled = plugins.IsEnabled(name),
            state = plugins.GetRuntimeState(name),
            restartRequired = true,
        }).ConfigureAwait(false);
    }
}
