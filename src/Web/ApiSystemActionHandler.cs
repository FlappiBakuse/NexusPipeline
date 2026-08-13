using System.Net;
using NexusPipeline.Services;

namespace NexusPipeline.Web;

/// <summary>
/// 系统操作（v0.6.3+）：取消队列完成操作（休眠/重启/关机）的 60 秒倒计时。
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
            await HttpHelper.WriteJsonAsync(context, new { error = "没有待执行的系统操作" }, 400).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
    }
}
