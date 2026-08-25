using System.Net;
using System.Globalization;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.Web;

[ApiRoute("history")]
internal static class ApiHistoryHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg)
    {
        if (method != "GET")
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        // 日期索引——范围内有记录的日期（倒序、含当日条数），供历史页左侧日期列表。
        if (seg.Length == 2 && seg[1].ToLowerInvariant() == "dates")
        {
            int rangeDays = int.TryParse(context.Request.QueryString["days"], out int rangeD) ? rangeD : 3;
            if (rangeDays < 1)
            {
                rangeDays = 1;
            }
            if (rangeDays > Limits.Current.MaxHistoryRetentionDays)
            {
                rangeDays = Limits.Current.MaxHistoryRetentionDays;
            }
            List<IGrouping<string, RunRecord>> groups = RuntimeContext.Instance.History.Query(
                DateTime.Today.AddDays(-(rangeDays - 1)), DateTime.Now.AddMinutes(5))
                .GroupBy(record => record.StartTime.ToString("yyyy-MM-dd"))
                .OrderByDescending(group => group.Key)
                .ToList();
            Audit.Log(Audit.Web, "查询历史记录", $"{groups.Count} 个日期（{rangeDays} 天）");
            await HttpHelper.WriteJsonAsync(context, new
            {
                dates = groups.Select(group => new { date = group.Key, count = group.Count() }).ToList(),
            }).ConfigureAwait(false);
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
            bool includeFull = string.Equals(context.Request.QueryString["full"], "true", StringComparison.OrdinalIgnoreCase)
                || context.Request.QueryString["full"] == "1";
            string fullAttempt = context.Request.QueryString["attempt"] ?? "";
            var attemptLogs = new List<object>();
            foreach (RunAttempt attempt in record.AttemptDetails)
            {
                var log = RuntimeContext.Instance.History.ReadScriptLog(record, attempt.Number);
                attemptLogs.Add(new
                {
                    number = attempt.Number,
                    logTail = log is null ? null : TextRules.TakeTail(log.Value.LogText, 200),
                    logTotalLines = log?.TotalLines ?? 0,
                    logText = includeFull && (string.IsNullOrWhiteSpace(fullAttempt) || fullAttempt == attempt.Number.ToString())
                        ? log?.LogText
                        : null,
                });
            }
            await HttpHelper.WriteJsonAsync(context, new
            {
                record,
                attemptLogs,
            }).ConfigureAwait(false);
            return;
        }
        // 按日期取记录——当日全部记录按开始时间升序（顺序执行），附 historyDir 供前端展示记录文件绝对路径。
        string? dateParam = context.Request.QueryString["date"];
        if (!string.IsNullOrWhiteSpace(dateParam))
        {
            if (!DateTime.TryParseExact(dateParam, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day))
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "date 参数格式须为 yyyy-MM-dd" }, 400).ConfigureAwait(false);
                return;
            }
            List<RunRecord> dayRecords = RuntimeContext.Instance.History.Query(day, day.AddDays(1).AddTicks(-1));
            Audit.Log(Audit.Web, "查询历史记录", $"{dayRecords.Count} 条（{day:yyyy-MM-dd}）");
            await HttpHelper.WriteJsonAsync(context, new
            {
                date = day.ToString("yyyy-MM-dd"),
                historyDir = AppPaths.HistoryDir,
                records = dayRecords.OrderBy(record => record.StartTime).ToList(),
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
        if (days > Limits.Current.MaxHistoryRetentionDays)
        {
            days = Limits.Current.MaxHistoryRetentionDays;
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
