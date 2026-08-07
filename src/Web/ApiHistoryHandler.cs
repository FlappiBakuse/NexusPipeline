using System.Net;

namespace NexusPipeline.Web;

internal static class ApiHistoryHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg)
    {
        if (method != "GET")
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        if (seg.Length == 2 && seg[1].ToLowerInvariant() == "detail")
        {
            string id = context.Request.QueryString["id"] ?? "";
            if (string.IsNullOrWhiteSpace(id))
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "缺少记录 ID" }, 400).ConfigureAwait(false);
                return;
            }
            RunRecord? record = RuntimeContext.Instance.History.FindById(id);
            if (record is null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "记录不存在" }, 404).ConfigureAwait(false);
                return;
            }
            Audit.Log(Audit.Web, "查询运行详情", $"{record.ScriptName}（{record.StartTime:yyyy-MM-dd HH:mm:ss}）");
            var log = RuntimeContext.Instance.History.ReadScriptLog(record);
            await HttpHelper.WriteJsonAsync(context, new
            {
                record,
                logTail = log is null ? null : TextRules.TakeTail(log.Value.LogText, 200),
                logTotalLines = log?.TotalLines ?? 0,
            }).ConfigureAwait(false);
            return;
        }
        string? scriptId = context.Request.QueryString["scriptId"];
        string? queueId = context.Request.QueryString["queueId"];
        int days = int.TryParse(context.Request.QueryString["days"], out int d) ? d : 3;
        if (days < 1)
        {
            days = 1;
        }
        if (days > 31)
        {
            days = 31;
        }
        List<RunRecord> records = RuntimeContext.Instance.History.Query(
            DateTime.Today.AddDays(-(days - 1)), DateTime.Now.AddMinutes(5),
            string.IsNullOrWhiteSpace(scriptId) ? null : scriptId,
            string.IsNullOrWhiteSpace(queueId) ? null : queueId);
        bool paged = context.Request.QueryString["offset"] is not null || context.Request.QueryString["limit"] is not null;
        if (paged)
        {
            int offset = int.TryParse(context.Request.QueryString["offset"], out int o) ? Math.Max(0, o) : 0;
            int limit = int.TryParse(context.Request.QueryString["limit"], out int l) ? Math.Max(1, l) : 20;
            Audit.Log(Audit.Web, "查询历史记录", $"{records.Count} 条（{days} 天，分页 offset={offset} limit={limit}）");
            await HttpHelper.WriteJsonAsync(context, new { total = records.Count, records = records.Skip(offset).Take(limit).ToList() }).ConfigureAwait(false);
            return;
        }
        Audit.Log(Audit.Web, "查询历史记录", $"{records.Count} 条（{days} 天）");
        await HttpHelper.WriteJsonAsync(context, records).ConfigureAwait(false);
    }
}
