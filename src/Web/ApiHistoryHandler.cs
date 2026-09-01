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
        if (seg.Length == 2 && seg[1].Equals("image", StringComparison.OrdinalIgnoreCase))
        {
            string id = context.Request.QueryString["id"] ?? "";
            string screenshotId = context.Request.QueryString["screenshot"] ?? "";
            if (!int.TryParse(context.Request.QueryString["attempt"], out int attemptNumber)
                || string.IsNullOrWhiteSpace(id)
                || string.IsNullOrWhiteSpace(screenshotId))
            {
                await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                return;
            }
            RunRecord? record = RuntimeContext.Instance.History.FindById(id);
            byte[]? image = record is null
                ? null
                : RuntimeContext.Instance.History.ReadScreenshot(record, attemptNumber, screenshotId);
            if (image is null)
            {
                await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                return;
            }
            await HttpHelper.WriteBinaryAsync(
                context,
                image,
                "image/jpeg",
                new Dictionary<string, string>
                {
                    ["Referrer-Policy"] = "no-referrer",
                }).ConfigureAwait(false);
            return;
        }
        // 日期索引——范围内有记录的日期（倒序、含当日条数），供历史页左侧日期列表。
        if (seg.Length == 2 && seg[1].ToLowerInvariant() == "dates")
        {
            bool hasExplicitRange = TryParseDateRange(context.Request, out DateTime rangeStart, out DateTime rangeEnd, out string? rangeError);
            if (hasExplicitRange && rangeError is not null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = rangeError }, 400).ConfigureAwait(false);
                return;
            }
            int rangeDays;
            string rangeLabel;
            if (!hasExplicitRange)
            {
                rangeDays = int.TryParse(context.Request.QueryString["days"], out int rangeD) ? rangeD : 3;
                if (rangeDays < 1)
                {
                    rangeDays = 1;
                }
                if (rangeDays > AppFixedLimits.HistoryRetentionDaysMax)
                {
                    rangeDays = AppFixedLimits.HistoryRetentionDaysMax;
                }
                rangeStart = DateTime.Today.AddDays(-(rangeDays - 1));
                rangeEnd = DateTime.Now.AddMinutes(5);
                rangeLabel = $"{rangeDays} 天";
            }
            else
            {
                rangeDays = (int)(rangeEnd.Date - rangeStart.Date).TotalDays + 1;
                rangeLabel = $"{rangeStart:yyyy-MM-dd} 至 {rangeEnd:yyyy-MM-dd}";
            }
            List<IGrouping<string, RunRecord>> groups = RuntimeContext.Instance.History.Query(
                rangeStart, rangeEnd)
                .GroupBy(record => record.StartTime.ToString("yyyy-MM-dd"))
                .OrderByDescending(group => group.Key)
                .ToList();
            Audit.Log(Audit.Web, "查询历史记录", $"{groups.Count} 个日期（{rangeLabel}）");
            await HttpHelper.WriteJsonAsync(context, new
            {
                dates = groups.Select(group => new { date = group.Key, count = group.Count() }).ToList(),
            }).ConfigureAwait(false);
            return;
        }
        // 当天用户索引——日期点击后才查询，运行明细继续由具体用户点击触发。
        if (seg.Length == 2 && seg[1].ToLowerInvariant() == "users")
        {
            string userDateParam = context.Request.QueryString["date"] ?? "";
            if (!DateTime.TryParseExact(userDateParam, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day))
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "date 参数格式须为 yyyy-MM-dd" }, 400).ConfigureAwait(false);
                return;
            }
            List<HistoryUserSummary> users = RuntimeContext.Instance.History.QueryUsers(day);
            Audit.Log(Audit.Web, "查询历史用户", $"{day:yyyy-MM-dd}：{users.Count} 个用户");
            await HttpHelper.WriteJsonAsync(context, new
            {
                date = day.ToString("yyyy-MM-dd"),
                users,
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
                    screenshots = (attempt.Screenshots ?? new List<RunHistoryScreenshot>()).Select(screenshot => new
                    {
                        id = screenshot.Id,
                        fileName = screenshot.FileName,
                        capturedAt = screenshot.CapturedAt,
                        width = screenshot.Width,
                        height = screenshot.Height,
                        source = screenshot.Source,
                        trigger = screenshot.Trigger,
                        ordinal = screenshot.Ordinal,
                        imageUrl = $"/api/history/image?id={Uri.EscapeDataString(record.Id)}&attempt={attempt.Number}&screenshot={Uri.EscapeDataString(screenshot.Id)}",
                    }).ToList(),
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
            string? userKey = context.Request.QueryString["userKey"];
            if (!string.IsNullOrWhiteSpace(userKey) && !HistoryService.IsValidUserKey(userKey))
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "userKey 参数格式无效" }, 400).ConfigureAwait(false);
                return;
            }
            List<RunRecord> dayRecords = RuntimeContext.Instance.History.Query(
                day,
                day.AddDays(1).AddTicks(-1),
                userKey: string.IsNullOrWhiteSpace(userKey) ? null : userKey);
            Audit.Log(Audit.Web, "查询历史记录", $"{dayRecords.Count} 条（{day:yyyy-MM-dd}）");
            await HttpHelper.WriteJsonAsync(context, new
            {
                date = day.ToString("yyyy-MM-dd"),
                userKey,
                historyDir = AppPaths.HistoryDir,
                records = dayRecords.OrderBy(record => record.StartTime).ToList(),
            }).ConfigureAwait(false);
            return;
        }
        string? scriptId = context.Request.QueryString["scriptId"];
        string? queueId = context.Request.QueryString["queueId"];
        bool hasHistoryRange = TryParseDateRange(context.Request, out DateTime historyStart, out DateTime historyEnd, out string? historyRangeError);
        if (hasHistoryRange && historyRangeError is not null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error = historyRangeError }, 400).ConfigureAwait(false);
            return;
        }
        int days;
        string historyRangeLabel;
        if (!hasHistoryRange)
        {
            days = int.TryParse(context.Request.QueryString["days"], out int d) ? d : 3;
            if (days < 1)
            {
                days = 1;
            }
            if (days > AppFixedLimits.HistoryRetentionDaysMax)
            {
                days = AppFixedLimits.HistoryRetentionDaysMax;
            }
            historyStart = DateTime.Today.AddDays(-(days - 1));
            historyEnd = DateTime.Now.AddMinutes(5);
            historyRangeLabel = $"{days} 天";
        }
        else
        {
            days = (int)(historyEnd.Date - historyStart.Date).TotalDays + 1;
            historyRangeLabel = $"{historyStart:yyyy-MM-dd} 至 {historyEnd:yyyy-MM-dd}";
        }
        List<RunRecord> records = RuntimeContext.Instance.History.Query(
            historyStart, historyEnd,
            string.IsNullOrWhiteSpace(scriptId) ? null : scriptId,
            string.IsNullOrWhiteSpace(queueId) ? null : queueId);
        bool paged = context.Request.QueryString["offset"] is not null || context.Request.QueryString["limit"] is not null;
        if (paged)
        {
            int offset = int.TryParse(context.Request.QueryString["offset"], out int o) ? Math.Max(0, o) : 0;
            int limit = int.TryParse(context.Request.QueryString["limit"], out int l) ? Math.Max(1, l) : 20;
            Audit.Log(Audit.Web, "查询历史记录", $"{records.Count} 条（{historyRangeLabel}，分页 offset={offset} limit={limit}）");
            await HttpHelper.WriteJsonAsync(context, new { total = records.Count, records = records.Skip(offset).Take(limit).ToList() }).ConfigureAwait(false);
            return;
        }
        Audit.Log(Audit.Web, "查询历史记录", $"{records.Count} 条（{historyRangeLabel}）");
        await HttpHelper.WriteJsonAsync(context, records).ConfigureAwait(false);
    }

    private static bool TryParseDateRange(HttpListenerRequest request, out DateTime start, out DateTime end, out string? error)
    {
        start = default;
        end = default;
        error = null;
        string fromParam = request.QueryString["from"] ?? "";
        string toParam = request.QueryString["to"] ?? "";
        bool hasFrom = !string.IsNullOrWhiteSpace(fromParam);
        bool hasTo = !string.IsNullOrWhiteSpace(toParam);
        if (!hasFrom && !hasTo)
        {
            return false;
        }
        if (!hasFrom || !hasTo)
        {
            error = "from 与 to 参数必须同时提供";
            return true;
        }
        if (!DateTime.TryParseExact(fromParam, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime from)
            || !DateTime.TryParseExact(toParam, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime to))
        {
            error = "日期范围格式须为 yyyy-MM-dd";
            return true;
        }
        if (to.Date < from.Date)
        {
            error = "结束日期不能早于开始日期";
            return true;
        }
        int rangeDays = (int)(to.Date - from.Date).TotalDays + 1;
        if (rangeDays > AppFixedLimits.HistoryRetentionDaysMax)
        {
            error = $"日期范围不能超过 {AppFixedLimits.HistoryRetentionDaysMax} 天";
            return true;
        }
        start = from.Date;
        end = to.Date.AddDays(1).AddTicks(-1);
        return true;
    }
}
