using System.Net;
using NexusPipeline.Services;

namespace NexusPipeline.Web;

/// <summary>
/// 系统操作：取消队列完成操作（休眠/重启/关机）的 60 秒倒计时。
/// 子路由用 seg 判断（cancel），不得用方法级 [ApiRoute("cancel")]——会与既有 /api/cancel 路由冲突。
/// </summary>
[ApiRoute("system-action")]
internal static class ApiSystemActionHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body)
    {
        if (method != "POST" || seg.Length < 2 || !seg[1].Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        if (!RuntimeContext.Instance.Center.CancelSystemAction())
        {
            await HttpHelper.ErrorAsync(context, "system_action_not_pending", 400).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
    }
}
