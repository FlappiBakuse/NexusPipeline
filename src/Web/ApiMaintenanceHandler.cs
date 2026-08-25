using System.Net;
using NexusPipeline.Services;

namespace NexusPipeline.Web;

/// <summary>
/// 维护 API：历史用户名目录的确认式清理入口（惰性遗留数据）。
/// 远程访问时沿用 WebServer 统一 Bearer 令牌保护；本地请求豁免。
/// </summary>
[ApiRoute("maintenance")]
internal static class ApiMaintenanceHandler
{
    /// <summary>GET /api/maintenance/legacy-users → 遗留目录候选清单；DELETE 同一路径按 scriptId/userKey 清理。</summary>
    public static async Task Handle(HttpListenerContext context, string method, string[] seg)
    {
        if (seg.Length != 2 || !string.Equals(seg[1], "legacy-users", StringComparison.OrdinalIgnoreCase))
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        RuntimeContext ctx = RuntimeContext.Instance;
        UserDataPruner pruner = ctx.Resolve<UserDataPruner>();
        if (method == "GET")
        {
            IReadOnlyList<LegacyDataCandidate> candidates = pruner.FindCandidates();
            await HttpHelper.WriteJsonAsync(context, new
            {
                candidates = candidates.Select(item => new
                {
                    scriptId = item.ScriptId,
                    userKey = item.UserKey,
                    itemCount = item.ItemCount,
                }).ToList(),
            }).ConfigureAwait(false);
            return;
        }
        if (method == "DELETE")
        {
            string? scriptId = context.Request.QueryString["scriptId"];
            string? userKey = context.Request.QueryString["userKey"];
            if (string.IsNullOrWhiteSpace(scriptId) || string.IsNullOrWhiteSpace(userKey))
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "缺少 scriptId 或 userKey 查询参数" }, 400).ConfigureAwait(false);
                return;
            }
            PruneResult result = pruner.Prune(scriptId!, userKey!, Audit.Web);
            if (result.Succeeded)
            {
                await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
                return;
            }
            bool busy = result.Code is "running" or "editing" or "locked" or "bound";
            await HttpHelper.WriteJsonAsync(context, new { ok = false, error = result.Error, code = result.Code }, busy ? 409 : 400).ConfigureAwait(false);
            return;
        }
        await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
    }
}