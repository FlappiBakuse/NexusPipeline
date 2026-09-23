using System.Net;
using System.Globalization;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Text;

namespace NexusPipeline.ControlPlane.Http;

[ApiRoute("history")]
internal static class ApiHistoryHandler
{
    public static async Task Handle(
        HttpListenerContext context,
        string method,
        string[] seg,
        RunHistoryService history,
        PluginManager plugins)
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
            RunRecord? record = history.FindById(id);
            byte[]? image = record is null
                ? null
                : history.ReadScreenshot(record, attemptNumber, screenshotId);
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
        if (seg.Length == 2 && seg[1].Equals("summary", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveHistoryRange(context.Request, out DateTime summaryStart, out DateTime summaryEnd, out string summaryRangeLabel, out string? summaryRangeError))
            {
                await HttpHelper.ErrorAsync(context, summaryRangeError!, 400).ConfigureAwait(false);
                return;
            }
            if (!TryReadHistoryFilters(context.Request, out string? summaryScriptId, out string? summaryQueueId, out string? summaryUserKey, out string? summaryStatus, out string? summaryFilterError))
            {
                await HttpHelper.ErrorAsync(context, summaryFilterError!, 400).ConfigureAwait(false);
                return;
            }
            HistorySummary summary = history.Summarize(
                summaryStart,
                summaryEnd,
                summaryScriptId,
                summaryQueueId,
                summaryUserKey,
                summaryStatus);
            Audit.Log(Audit.Web, "查询历史摘要", $"{summary.TotalCount} 条（{summaryRangeLabel}）");
            await HttpHelper.WriteJsonAsync(context, summary).ConfigureAwait(false);
            return;
        }
        // 日期索引——范围内有记录的日期（倒序、含当日条数），供历史页左侧日期列表。
        if (seg.Length == 2 && seg[1].Equals("dates", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveHistoryRange(context.Request, out DateTime rangeStart, out DateTime rangeEnd, out string rangeLabel, out string? rangeError))
            {
                await HttpHelper.ErrorAsync(context, rangeError!, 400).ConfigureAwait(false);
                return;
            }
            if (!TryReadHistoryFilters(context.Request, out string? scriptId, out string? queueId, out string? userKey, out string? status, out string? filterError))
            {
                await HttpHelper.ErrorAsync(context, filterError!, 400).ConfigureAwait(false);
                return;
            }
            List<IGrouping<string, RunRecord>> groups = history.Query(
                rangeStart,
                rangeEnd,
                scriptId,
                queueId,
                userKey,
                status)
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
        if (seg.Length == 2 && seg[1].Equals("users", StringComparison.OrdinalIgnoreCase))
        {
            string userDateParam = context.Request.QueryString["date"] ?? "";
            if (!DateTime.TryParseExact(userDateParam, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day))
            {
                await HttpHelper.ErrorAsync(context, "history_invalid_date", 400).ConfigureAwait(false);
                return;
            }
            if (!TryReadHistoryFilters(context.Request, out string? scriptId, out string? queueId, out _, out string? status, out string? filterError))
            {
                await HttpHelper.ErrorAsync(context, filterError!, 400).ConfigureAwait(false);
                return;
            }
            List<HistoryUserSummary> users = history.QueryUsers(day, scriptId, queueId, status);
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
                await HttpHelper.ErrorAsync(context, "history_id_required", 400).ConfigureAwait(false);
                return;
            }
            RunRecord? record = history.FindById(id);
            if (record is null)
            {
                await HttpHelper.ErrorAsync(context, "history_not_found", 404).ConfigureAwait(false);
                return;
            }
            record = record.Clone();
            plugins.LocalizeHistory(record, context.Request.Locale);
            System.Text.Json.Nodes.JsonObject recordView = RunHistoryService.ToView(record);
            if (context.Request.QueryString["metadata"] == "true")
            {
                await HttpHelper.WriteJsonAsync(context, new { record = recordView }).ConfigureAwait(false);
                return;
            }
            Audit.Log(Audit.Web, "查询运行详情", $"{record.ScriptName}（{record.StartTime:yyyy-MM-dd HH:mm:ss}）");
            bool includeFull = string.Equals(context.Request.QueryString["full"], "true", StringComparison.OrdinalIgnoreCase)
                || context.Request.QueryString["full"] == "1";
            string fullAttempt = context.Request.QueryString["attempt"] ?? "";
            var attemptLogs = new List<object>();
            foreach (RunAttempt attempt in record.AttemptDetails)
            {
                var log = history.ReadScriptLog(record, attempt.Number);
                attemptLogs.Add(new
                {
                    number = attempt.Number,
                    durationMs = RunHistoryService.DurationMilliseconds(attempt),
                    logTail = log is null ? null : TextTail.TakeTail(log.Value.LogText, 200),
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
                record = recordView,
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
                await HttpHelper.ErrorAsync(context, "history_invalid_date", 400).ConfigureAwait(false);
                return;
            }
            if (!TryReadHistoryFilters(context.Request, out string? scriptId, out string? queueId, out string? userKey, out string? status, out string? filterError))
            {
                await HttpHelper.ErrorAsync(context, filterError!, 400).ConfigureAwait(false);
                return;
            }
            List<RunRecord> dayRecords = history.Query(
                day,
                day.AddDays(1).AddTicks(-1),
                scriptId,
                queueId,
                userKey,
                status);
            Audit.Log(Audit.Web, "查询历史记录", $"{dayRecords.Count} 条（{day:yyyy-MM-dd}）");
            await HttpHelper.WriteJsonAsync(context, new
            {
                date = day.ToString("yyyy-MM-dd"),
                userKey,
                historyDir = AppPaths.HistoryDir,
                records = dayRecords
                    .OrderBy(record => record.StartTime)
                    .Select(record =>
                    {
                        RunRecord localized = record.Clone();
                        plugins.LocalizeHistory(localized, context.Request.Locale);
                        return RunHistoryService.ToView(localized);
                    })
                    .ToList(),
            }).ConfigureAwait(false);
            return;
        }
        if (!TryReadHistoryFilters(context.Request, out string? genericScriptId, out string? genericQueueId, out string? genericUserKey, out string? genericStatus, out string? genericFilterError))
        {
            await HttpHelper.ErrorAsync(context, genericFilterError!, 400).ConfigureAwait(false);
            return;
        }
        if (!TryResolveHistoryRange(context.Request, out DateTime historyStart, out DateTime historyEnd, out string historyRangeLabel, out string? historyRangeError))
        {
            await HttpHelper.ErrorAsync(context, historyRangeError!, 400).ConfigureAwait(false);
            return;
        }
        List<RunRecord> records = history.Query(
            historyStart, historyEnd,
            genericScriptId,
            genericQueueId,
            genericUserKey,
            genericStatus);
        List<System.Text.Json.Nodes.JsonObject> recordViews = records.Select(record =>
        {
            RunRecord localized = record.Clone();
            plugins.LocalizeHistory(localized, context.Request.Locale);
            return RunHistoryService.ToView(localized);
        }).ToList();
        bool paged = context.Request.QueryString["offset"] is not null || context.Request.QueryString["limit"] is not null;
        if (paged)
        {
            int offset = int.TryParse(context.Request.QueryString["offset"], out int o) ? Math.Max(0, o) : 0;
            int limit = int.TryParse(context.Request.QueryString["limit"], out int l) ? Math.Max(1, l) : 20;
            Audit.Log(Audit.Web, "查询历史记录", $"{recordViews.Count} 条（{historyRangeLabel}，分页 offset={offset} limit={limit}）");
            await HttpHelper.WriteJsonAsync(context, new { total = recordViews.Count, records = recordViews.Skip(offset).Take(limit).ToList() }).ConfigureAwait(false);
            return;
        }
        Audit.Log(Audit.Web, "查询历史记录", $"{recordViews.Count} 条（{historyRangeLabel}）");
        await HttpHelper.WriteJsonAsync(context, recordViews).ConfigureAwait(false);
    }

    private static bool TryResolveHistoryRange(
        HttpListenerRequest request,
        out DateTime start,
        out DateTime end,
        out string rangeLabel,
        out string? error)
    {
        bool hasExplicitRange = TryParseDateRange(request, out start, out end, out error);
        if (hasExplicitRange)
        {
            rangeLabel = $"{start:yyyy-MM-dd} 至 {end:yyyy-MM-dd}";
            return error is null;
        }

        int days = int.TryParse(request.QueryString["days"], out int parsedDays) ? parsedDays : 3;
        days = Math.Clamp(days, 1, AppFixedLimits.HistoryRetentionDaysMax);
        start = DateTime.Today.AddDays(-(days - 1));
        end = DateTime.Now.AddMinutes(5);
        rangeLabel = $"{days} 天";
        error = null;
        return true;
    }

    private static bool TryReadHistoryFilters(
        HttpListenerRequest request,
        out string? scriptId,
        out string? queueId,
        out string? userKey,
        out string? status,
        out string? error)
    {
        scriptId = QueryValue(request, "scriptId");
        queueId = QueryValue(request, "queueId");
        userKey = QueryValue(request, "userKey");
        status = RunHistoryService.NormalizeStatus(request.QueryString["status"]);
        if (userKey is not null && !RunHistoryService.IsValidUserKey(userKey))
        {
            error = "history_invalid_user_key";
            return false;
        }
        if (!RunHistoryService.IsValidStatus(status))
        {
            error = "history_invalid_status";
            return false;
        }
        error = null;
        return true;
    }

    private static string? QueryValue(HttpListenerRequest request, string key)
    {
        string value = (request.QueryString[key] ?? "").Trim();
        return value.Length == 0 ? null : value;
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
            error = "history_range_required";
            return true;
        }
        if (!DateTime.TryParseExact(fromParam, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime from)
            || !DateTime.TryParseExact(toParam, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime to))
        {
            error = "history_invalid_date_range";
            return true;
        }
        if (to.Date < from.Date)
        {
            error = "history_range_reversed";
            return true;
        }
        int rangeDays = (int)(to.Date - from.Date).TotalDays + 1;
        if (rangeDays > AppFixedLimits.HistoryRetentionDaysMax)
        {
            error = "history_range_too_large";
            return true;
        }
        start = from.Date;
        end = to.Date.AddDays(1).AddTicks(-1);
        return true;
    }
}
