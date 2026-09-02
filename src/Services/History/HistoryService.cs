using System.Globalization;
using System.Text;
using System.Text.Json;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

internal sealed record HistoryUserSummary(
    string UserKey,
    string UserId,
    string UserName,
    int Count,
    int SuccessCount,
    int FailedCount,
    int PartialCount,
    int CancelledCount,
    int SkippedCount);

internal class HistoryService : IHistoryStore
{
    private const int ScreenshotCapacity = 8;
    private static readonly object Sync = new();

    private readonly string _historyDir;
    private readonly string _outputDir;
    private readonly string _logDir;

    public HistoryService(string? historyDir = null, string? outputDir = null, string? logDir = null)
    {
        _historyDir = Path.GetFullPath(historyDir ?? AppPaths.HistoryDir);
        _outputDir = Path.GetFullPath(outputDir ?? AppPaths.OutputDir);
        _logDir = Path.GetFullPath(logDir ?? AppPaths.LogDir);
    }

    /// <summary>
    /// 将旧版 /history/YYYY-MM-DD/*.json 与对应日志迁移到
    /// /history/YYYY-MM-DD/用户/脚本名称-HH-mm-ss/。迁移采用临时目录和复制提交，源文件在提交成功后才清理；
    /// 失败时保留旧文件，下一次启动仍可继续处理。
    /// </summary>
    public void MigrateLegacy()
    {
        lock (Sync)
        {
            if (!Directory.Exists(_historyDir))
            {
                return;
            }

            foreach (string dayDir in Directory.GetDirectories(_historyDir))
            {
                string dayName = Path.GetFileName(dayDir);
                if (!DateTime.TryParseExact(
                        dayName,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out _))
                {
                    continue;
                }
                MigrateLegacyDay(dayDir);
            }
        }
    }

    /// <summary>保存运行历史：每个运行拥有独立目录，JSON、Attempt 日志和截图一起提交。</summary>
    public HistorySaveResult Save(
        RunRecord record,
        List<string> attemptLogs,
        IReadOnlyList<RunScreenshot> screenshots)
    {
        if (record.StartTime == DateTime.MinValue)
        {
            return new HistorySaveResult(record.Clone(), null);
        }

        RunRecord persisted = record.Clone();
        persisted.AttemptDetails ??= new List<RunAttempt>();
        attemptLogs ??= new List<string>();
        string? temporaryDir = null;
        try
        {
            lock (Sync)
            {
                string dateName = persisted.StartTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                string dayDir = Path.Combine(_historyDir, dateName);
                string userDirName = SafeSegment(persisted.UserName, "未指定用户");
                string userDir = Path.Combine(dayDir, userDirName);
                Directory.CreateDirectory(userDir);

                string timeName = persisted.StartTime.ToString("HH-mm-ss", CultureInfo.InvariantCulture);
                string scriptName = SafeSegment(persisted.ScriptName, "未命名脚本");
                string runDirBaseName = $"{scriptName}-{timeName}";
                string runDirName = FindFreeDirectoryName(userDir, runDirBaseName);
                string finalDir = Path.Combine(userDir, runDirName);
                temporaryDir = Path.Combine(userDir, $".{runDirName}.{Guid.NewGuid():N}.tmp");
                Directory.CreateDirectory(temporaryDir);

                persisted.HistoryDirectory = Path.Combine(userDirName, runDirName);
                // 运行目录体现脚本名称，目录内文件继续使用时间主键，保持历史读取与导出兼容。
                persisted.LogFile = $"{timeName}.json";

                IReadOnlyList<RunScreenshot> historyScreenshots = screenshots ?? Array.Empty<RunScreenshot>();
                foreach (RunAttempt attempt in persisted.AttemptDetails)
                {
                    int attemptNumber = Math.Max(1, attempt.Number);
                    string logName = $"{timeName}-{attemptNumber}.log";
                    attempt.LogFile = logName;
                    string logText = attempt.Number > 0 && attempt.Number <= attemptLogs.Count
                        ? attemptLogs[attempt.Number - 1]
                        : "";
                    if (string.IsNullOrWhiteSpace(logText))
                    {
                        logText = "（未配置日志路径或未监控到脚本日志）" + Environment.NewLine;
                    }
                    File.WriteAllText(
                        Path.Combine(temporaryDir, logName),
                        logText,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

                    List<RunScreenshot> attemptScreenshots = historyScreenshots
                        .Where(item => item.AttemptNumber == attemptNumber)
                        .OrderBy(item => item.Ordinal)
                        .TakeLast(ScreenshotCapacity)
                        .ToList();
                    attempt.Screenshots = new List<RunHistoryScreenshot>(attemptScreenshots.Count);
                    for (int index = 0; index < attemptScreenshots.Count; index++)
                    {
                        RunScreenshot screenshot = attemptScreenshots[index];
                        string imageName = $"{timeName}-{attemptNumber}-s{index + 1}.jpg";
                        File.WriteAllBytes(Path.Combine(temporaryDir, imageName), screenshot.Data);
                        attempt.Screenshots.Add(new RunHistoryScreenshot
                        {
                            Id = screenshot.Id,
                            FileName = imageName,
                            CapturedAt = screenshot.CapturedAt,
                            Width = screenshot.Width,
                            Height = screenshot.Height,
                            Source = screenshot.Source,
                            Trigger = screenshot.Trigger,
                            Ordinal = screenshot.Ordinal,
                        });
                    }
                }

                // JSON 是运行目录的提交标记：全部日志和图片写入临时目录后才生成。
                JsonUtil.WriteAtomic(
                    Path.Combine(temporaryDir, persisted.LogFile),
                    JsonSerializer.Serialize(persisted, JsonOpts.Indented));
                Directory.Move(temporaryDir, finalDir);
                temporaryDir = null;
            }
            return new HistorySaveResult(persisted.Clone(), null);
        }
        catch (Exception ex)
        {
            if (temporaryDir is not null)
            {
                TryDeleteTemporaryDirectory(temporaryDir);
            }
            Logger.Warn($"[警告] 保存运行历史失败：{ex.Message}");
            return new HistorySaveResult(persisted.Clone(), $"保存运行历史失败：{ex.Message}");
        }
    }

    /// <summary>兼容只保存状态与日志的内部调用。</summary>
    public HistorySaveResult Save(RunRecord record, List<string> attemptLogs) =>
        Save(record, attemptLogs, Array.Empty<RunScreenshot>());

    /// <summary>读取某天的运行记录；只识别新的「用户/运行目录」两级结构。</summary>
    private List<RunRecord> ReadDayRecords(DateTime date)
    {
        var records = new List<RunRecord>();
        string dayDir = Path.Combine(_historyDir, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (!Directory.Exists(dayDir))
        {
            return records;
        }

        foreach (string userDir in Directory.GetDirectories(dayDir))
        {
            if (Path.GetFileName(userDir).StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }
            foreach (string runDir in Directory.GetDirectories(userDir))
            {
                if (Path.GetFileName(runDir).StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }
                foreach (string file in Directory.GetFiles(runDir, "*.json", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        RunRecord? record = JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(file), JsonOpts.Default);
                        if (record is null)
                        {
                            continue;
                        }
                        if (string.IsNullOrWhiteSpace(record.HistoryDirectory))
                        {
                            record.HistoryDirectory = Path.GetRelativePath(dayDir, runDir);
                        }
                        if (string.IsNullOrWhiteSpace(record.LogFile))
                        {
                            record.LogFile = Path.GetFileName(file);
                        }
                        record.AttemptDetails ??= new List<RunAttempt>();
                        records.Add(record);
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"历史记录文件解析失败已跳过（{file}）：{ex.Message}");
                    }
                }
            }
        }
        return records;
    }

    public IReadOnlyDictionary<string, int> GetSuccessfulRunsByUser(DateTime date, string scriptInstanceId)
    {
        lock (Sync)
        {
            return CountSuccessfulRunsByUser(ReadDayRecords(date), date, scriptInstanceId);
        }
    }

    internal static IReadOnlyDictionary<string, int> CountSuccessfulRunsByUser(
        IEnumerable<RunRecord> records,
        DateTime date,
        string scriptInstanceId)
    {
        return records
            .Where(record => record.StartTime.Date == date.Date
                && string.Equals(record.ScriptInstanceId, scriptInstanceId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(record.UserId)
                && string.Equals(
                    string.IsNullOrWhiteSpace(record.FinalStatus) ? record.Status : record.FinalStatus,
                    "success",
                    StringComparison.OrdinalIgnoreCase))
            .GroupBy(record => record.UserId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    }

    /// <summary>按日期聚合当天实际出现过的运行用户。</summary>
    public List<HistoryUserSummary> QueryUsers(DateTime date, string? scriptId = null, string? queueId = null)
    {
        lock (Sync)
        {
            return SummarizeUsers(ReadDayRecords(date), date, scriptId, queueId);
        }
    }

    internal static List<HistoryUserSummary> SummarizeUsers(
        IEnumerable<RunRecord> records,
        DateTime date,
        string? scriptId = null,
        string? queueId = null)
    {
        return records
            .Where(record => record.StartTime.Date == date.Date && Matches(record, scriptId, queueId, null))
            .GroupBy(GetUserKey, StringComparer.Ordinal)
            .Select(group =>
            {
                RunRecord latest = group.OrderByDescending(record => record.StartTime).First();
                string userId = group.Select(record => (record.UserId ?? "").Trim())
                    .FirstOrDefault(value => value.Length > 0) ?? "";
                string userName = group.Select(record => (record.UserName ?? "").Trim())
                    .FirstOrDefault(value => value.Length > 0) ?? "";
                if (userName.Length == 0)
                {
                    userName = userId.Length == 0 ? "未指定用户" : userId;
                }
                return new
                {
                    UserKey = group.Key,
                    UserId = userId,
                    UserName = userName,
                    Count = group.Count(),
                    SuccessCount = group.Count(record => StatusOf(record) == "success"),
                    FailedCount = group.Count(record => StatusOf(record) == "failed"),
                    PartialCount = group.Count(record => StatusOf(record) == "partial"),
                    CancelledCount = group.Count(record => StatusOf(record) == "cancelled"),
                    SkippedCount = group.Count(record => StatusOf(record) == "skipped"),
                    LatestStart = latest.StartTime,
                };
            })
            .OrderByDescending(item => item.LatestStart)
            .ThenBy(item => item.UserName, StringComparer.OrdinalIgnoreCase)
            .Select(item => new HistoryUserSummary(
                item.UserKey,
                item.UserId,
                item.UserName,
                item.Count,
                item.SuccessCount,
                item.FailedCount,
                item.PartialCount,
                item.CancelledCount,
                item.SkippedCount))
            .ToList();
    }

    public List<RunRecord> Query(
        DateTime start,
        DateTime end,
        string? scriptId = null,
        string? queueId = null,
        string? userKey = null)
    {
        var result = new List<RunRecord>();
        lock (Sync)
        {
            for (DateTime date = start.Date; date <= end.Date; date = date.AddDays(1))
            {
                foreach (RunRecord record in ReadDayRecords(date))
                {
                    if (record.StartTime < start || record.StartTime > end)
                    {
                        continue;
                    }
                    if (Matches(record, scriptId, queueId, userKey))
                    {
                        result.Add(record);
                    }
                }
            }
        }
        return result.OrderByDescending(record => record.StartTime).ToList();
    }

    internal static string GetUserKey(RunRecord record)
    {
        string userId = (record.UserId ?? "").Trim();
        string userName = (record.UserName ?? "").Trim();
        return userId.Length == 0
            ? "legacy:" + userName
            : "id:" + userId;
    }

    internal static bool IsValidUserKey(string? userKey)
    {
        return !string.IsNullOrWhiteSpace(userKey)
            && (userKey.StartsWith("id:", StringComparison.Ordinal)
                || userKey.StartsWith("legacy:", StringComparison.Ordinal));
    }

    private static bool Matches(RunRecord record, string? scriptId, string? queueId, string? userKey)
    {
        if (!string.IsNullOrWhiteSpace(scriptId) && record.ScriptInstanceId != scriptId)
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(queueId) && record.QueueId != queueId)
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(userKey) && !string.Equals(GetUserKey(record), userKey, StringComparison.Ordinal))
        {
            return false;
        }
        return true;
    }

    private static string StatusOf(RunRecord record)
    {
        string status = string.IsNullOrWhiteSpace(record.FinalStatus) ? record.Status ?? "" : record.FinalStatus;
        return status.Trim().ToLowerInvariant();
    }

    /// <summary>按 Id 查找历史记录；默认窗口与历史保留上限一致。</summary>
    public RunRecord? FindById(string id, int days = 0)
    {
        if (days <= 0)
        {
            days = AppFixedLimits.HistoryRetentionDaysMax;
        }
        lock (Sync)
        {
            for (int offset = days - 1; offset >= 0; offset--)
            {
                DateTime date = DateTime.Today.AddDays(-offset);
                foreach (RunRecord record in ReadDayRecords(date))
                {
                    if (record.Id == id)
                    {
                        return record;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>读取某次尝试的脚本日志。</summary>
    public (string LogText, int TotalLines)? ReadScriptLog(RunRecord record, int attemptNo)
    {
        RunAttempt? attempt = (record.AttemptDetails ?? new List<RunAttempt>()).FirstOrDefault(a => a.Number == attemptNo);
        string? text = attempt is null ? null : ReadRunFile(record, attempt.LogFile);
        return text is null ? null : (text, CountLines(text));
    }

    /// <summary>读取并校验历史截图；只允许访问记录元数据登记的文件。</summary>
    public byte[]? ReadScreenshot(RunRecord record, int attemptNo, string screenshotId)
    {
        if (string.IsNullOrWhiteSpace(screenshotId))
        {
            return null;
        }
        RunAttempt? attempt = (record.AttemptDetails ?? new List<RunAttempt>()).FirstOrDefault(a => a.Number == attemptNo);
        RunHistoryScreenshot? screenshot = attempt?.Screenshots?.FirstOrDefault(item =>
            string.Equals(item.Id, screenshotId.Trim(), StringComparison.OrdinalIgnoreCase));
        return screenshot is null ? null : ReadRunBytes(record, screenshot.FileName);
    }

    public List<RunRecord> Recent(int days = 3)
    {
        return Query(DateTime.Today.AddDays(-(days - 1)), DateTime.Now.AddMinutes(5));
    }

    public void Cleanup(int retentionDays)
    {
        if (retentionDays < 1)
        {
            retentionDays = 3;
        }
        Logger.Info($"======== 清理：仅保留最近 {retentionDays} 天历史 ========");
        DateTime cutoff = DateTime.Today.AddDays(-(retentionDays - 1));
        int removed = 0;

        lock (Sync)
        {
            if (Directory.Exists(_historyDir))
            {
                foreach (string directory in Directory.GetDirectories(_historyDir))
                {
                    string name = Path.GetFileName(directory);
                    if (DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dirDate)
                        && dirDate < cutoff)
                    {
                        try
                        {
                            Directory.Delete(directory, recursive: true);
                            removed++;
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"[警告] 删除过期历史目录失败：{ex.Message}");
                        }
                    }
                }
            }

            foreach (string directory in new[] { _outputDir, _logDir })
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }
                foreach (string file in Directory.GetFiles(directory))
                {
                    DateTime fileDate;
                    try
                    {
                        fileDate = File.GetLastWriteTime(file).Date;
                    }
                    catch
                    {
                        continue;
                    }
                    if (fileDate < cutoff)
                    {
                        try
                        {
                            File.Delete(file);
                            removed++;
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }
        Logger.Info($"清理完成，共删除 {removed} 个过期项。");
    }

    private void MigrateLegacyDay(string dayDir)
    {
        string[] jsonFiles = Directory.GetFiles(dayDir, "*.json", SearchOption.TopDirectoryOnly);
        var claimedLogs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string jsonFile in jsonFiles)
        {
            MigrateFlatHistoryRecord(dayDir, jsonFile, claimedLogs);
        }

        MigrateLegacyNestedRuns(dayDir);

        // 没有 JSON 所属关系的旧日志仍从日期目录移出，避免继续混在旧协议根层。
        foreach (string orphan in Directory.GetFiles(dayDir, "*.log", SearchOption.TopDirectoryOnly))
        {
            if (claimedLogs.Contains(Path.GetFullPath(orphan)) || !File.Exists(orphan))
            {
                continue;
            }
            try
            {
                string userDir = Path.Combine(dayDir, "未指定用户");
                Directory.CreateDirectory(userDir);
                string stem = SafeSegment(Path.GetFileNameWithoutExtension(orphan), "孤立日志");
                string runDir = FindFreeDirectoryName(userDir, $"孤立日志-{stem}");
                string targetDir = Path.Combine(userDir, runDir);
                Directory.CreateDirectory(targetDir);
                File.Move(orphan, Path.Combine(targetDir, Path.GetFileName(orphan)));
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 孤立历史日志迁移失败，保留源文件（{orphan}）：{ex.Message}");
            }
        }
    }

    private void MigrateFlatHistoryRecord(string dayDir, string jsonFile, HashSet<string> claimedLogs)
    {
        RunRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(jsonFile), JsonOpts.Default);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 旧历史记录迁移失败，保留源文件（{jsonFile}）：{ex.Message}");
            return;
        }
        if (record is null)
        {
            Logger.Warn($"[警告] 旧历史记录迁移失败，保留源文件（{jsonFile}）：JSON 为空。");
            return;
        }

        record.AttemptDetails ??= new List<RunAttempt>();
        string userDirName = SafeSegment(record.UserName, "未指定用户");
        string userDir = Path.Combine(dayDir, userDirName);
        Directory.CreateDirectory(userDir);
        string timeName = HistoryTimeName(record, Path.GetFileNameWithoutExtension(jsonFile));
        string runDirName = FindFreeDirectoryName(userDir, BuildRunDirectoryBase(record.ScriptName, timeName));
        string finalDir = Path.Combine(userDir, runDirName);
        string temporaryDir = Path.Combine(userDir, $".{runDirName}.{Guid.NewGuid():N}.tmp");
        var sourceLogs = new List<string>();
        var referencedLogs = new List<(RunAttempt Attempt, string Source, string FileName)>();
        foreach (RunAttempt attempt in record.AttemptDetails)
        {
            string oldLogName = Path.GetFileName(attempt.LogFile);
            if (string.IsNullOrWhiteSpace(oldLogName))
            {
                oldLogName = $"{timeName}-{Math.Max(1, attempt.Number)}.log";
            }
            string sourceLog = Path.Combine(dayDir, oldLogName);
            if (IsDirectChild(dayDir, sourceLog))
            {
                string fullSourceLog = Path.GetFullPath(sourceLog);
                claimedLogs.Add(fullSourceLog);
                referencedLogs.Add((attempt, fullSourceLog, oldLogName));
            }
        }

        // Directory.Move 与源文件清理之间进程可能退出；已有同 ID 的完整目标说明迁移已提交，
        // 只需清理尚未删除的旧源，避免重试产生 -2 目录。
        string? existingDirectory = FindExistingRunDirectory(userDir, record.Id, null);
        if (existingDirectory is not null)
        {
            TryDeleteFile(jsonFile);
            foreach (var referencedLog in referencedLogs)
            {
                TryDeleteFile(referencedLog.Source);
            }
            return;
        }

        try
        {
            Directory.CreateDirectory(temporaryDir);
            foreach ((RunAttempt attempt, string sourceLog, string oldLogName) in referencedLogs)
            {
                attempt.LogFile = oldLogName;
                if (!File.Exists(sourceLog))
                {
                    continue;
                }
                File.Copy(sourceLog, Path.Combine(temporaryDir, oldLogName));
                sourceLogs.Add(sourceLog);
            }
            record.HistoryDirectory = Path.Combine(userDirName, runDirName);
            record.LogFile = Path.GetFileName(jsonFile);
            record.AttemptDetails.ForEach(attempt => attempt.Screenshots ??= new List<RunHistoryScreenshot>());
            JsonUtil.WriteAtomic(
                Path.Combine(temporaryDir, record.LogFile),
                JsonSerializer.Serialize(record, JsonOpts.Indented));
            Directory.Move(temporaryDir, finalDir);

            File.Delete(jsonFile);
            foreach (string sourceLog in sourceLogs)
            {
                TryDeleteFile(sourceLog);
            }
            Logger.Info($"已迁移旧历史：{Path.Combine(userDirName, runDirName)}");
        }
        catch (Exception ex)
        {
            TryDeleteTemporaryDirectory(temporaryDir);
            Logger.Warn($"[警告] 旧历史记录迁移失败，保留源文件（{jsonFile}）：{ex.Message}");
        }
    }

    private void MigrateLegacyNestedRuns(string dayDir)
    {
        foreach (string userDir in Directory.GetDirectories(dayDir))
        {
            if (Path.GetFileName(userDir).StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (string sourceRunDir in Directory.GetDirectories(userDir))
            {
                string sourceRunName = Path.GetFileName(sourceRunDir);
                if (sourceRunName.StartsWith(".", StringComparison.Ordinal) || !LooksLikeLegacyTimeDirectory(sourceRunName))
                {
                    continue;
                }

                string[] recordFiles = Directory.GetFiles(sourceRunDir, "*.json", SearchOption.TopDirectoryOnly);
                if (recordFiles.Length != 1)
                {
                    Logger.Warn($"[警告] 旧历史运行目录包含 {recordFiles.Length} 个 JSON，保留现场等待人工核查：{sourceRunDir}");
                    continue;
                }

                RunRecord? record;
                try
                {
                    record = JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(recordFiles[0]), JsonOpts.Default);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[警告] 旧历史运行目录迁移失败，保留源目录（{sourceRunDir}）：{ex.Message}");
                    continue;
                }
                if (record is null)
                {
                    Logger.Warn($"[警告] 旧历史运行目录迁移失败，保留源目录（{sourceRunDir}）：JSON 为空。");
                    continue;
                }

                record.AttemptDetails ??= new List<RunAttempt>();
                if (string.IsNullOrWhiteSpace(record.UserName))
                {
                    record.UserName = Path.GetFileName(userDir);
                }
                string userDirName = SafeSegment(record.UserName, "未指定用户");
                string targetUserDir = Path.Combine(dayDir, userDirName);
                Directory.CreateDirectory(targetUserDir);
                string? existingDirectory = FindExistingRunDirectory(targetUserDir, record.Id, sourceRunDir);
                if (existingDirectory is not null)
                {
                    try
                    {
                        Directory.Delete(sourceRunDir, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[警告] 已提交历史的旧源目录清理失败，保留源目录（{sourceRunDir}）：{ex.Message}");
                    }
                    continue;
                }
                string timeName = HistoryTimeName(record, sourceRunName);
                string targetRunName = FindFreeDirectoryName(targetUserDir, BuildRunDirectoryBase(record.ScriptName, timeName));
                string targetDir = Path.Combine(targetUserDir, targetRunName);
                string temporaryDir = Path.Combine(targetUserDir, $".{targetRunName}.{Guid.NewGuid():N}.tmp");
                try
                {
                    Directory.CreateDirectory(temporaryDir);
                    CopyDirectoryContents(sourceRunDir, temporaryDir);
                    record.HistoryDirectory = Path.Combine(userDirName, targetRunName);
                    record.LogFile = Path.GetFileName(recordFiles[0]);
                    foreach (RunAttempt attempt in record.AttemptDetails)
                    {
                        attempt.LogFile = Path.GetFileName(attempt.LogFile);
                        attempt.Screenshots ??= new List<RunHistoryScreenshot>();
                    }
                    JsonUtil.WriteAtomic(
                        Path.Combine(temporaryDir, record.LogFile),
                        JsonSerializer.Serialize(record, JsonOpts.Indented));
                    Directory.Move(temporaryDir, targetDir);
                    Directory.Delete(sourceRunDir, recursive: true);
                    Logger.Info($"已迁移旧历史：{Path.Combine(userDirName, targetRunName)}");
                }
                catch (Exception ex)
                {
                    TryDeleteTemporaryDirectory(temporaryDir);
                    Logger.Warn($"[警告] 旧历史运行目录迁移失败，保留源目录（{sourceRunDir}）：{ex.Message}");
                }
            }
        }
    }

    private static void CopyDirectoryContents(string sourceDir, string targetDir)
    {
        foreach (string directory in Directory.GetDirectories(sourceDir))
        {
            string childTarget = Path.Combine(targetDir, Path.GetFileName(directory));
            Directory.CreateDirectory(childTarget);
            CopyDirectoryContents(directory, childTarget);
        }
        foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)));
        }
    }

    private static string? FindExistingRunDirectory(string userDir, string? recordId, string? excludedDirectory)
    {
        if (string.IsNullOrWhiteSpace(recordId) || !Directory.Exists(userDir))
        {
            return null;
        }
        string? excluded = excludedDirectory is null ? null : Path.GetFullPath(excludedDirectory);
        foreach (string runDir in Directory.GetDirectories(userDir))
        {
            if (Path.GetFileName(runDir).StartsWith(".", StringComparison.Ordinal)
                || (excluded is not null && string.Equals(Path.GetFullPath(runDir), excluded, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            foreach (string jsonFile in Directory.GetFiles(runDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    RunRecord? existing = JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(jsonFile), JsonOpts.Default);
                    if (existing is not null && string.Equals(existing.Id, recordId, StringComparison.Ordinal))
                    {
                        return runDir;
                    }
                }
                catch
                {
                    // 单个目标记录损坏不应阻止其他旧历史继续迁移。
                }
            }
        }
        return null;
    }

    private static string BuildRunDirectoryBase(string? scriptName, string timeName) =>
        $"{SafeSegment(scriptName, "未命名脚本")}-{timeName}";

    private static string HistoryTimeName(RunRecord record, string fallback)
    {
        if (record.StartTime != DateTime.MinValue)
        {
            return record.StartTime.ToString("HH-mm-ss", CultureInfo.InvariantCulture);
        }

        string candidate = fallback.Length >= 8 ? fallback[..8] : fallback;
        return TimeSpan.TryParseExact(candidate, "hh\\-mm\\-ss", CultureInfo.InvariantCulture, out _)
            ? candidate
            : "00-00-00";
    }

    private static bool LooksLikeLegacyTimeDirectory(string name)
    {
        if (name.Length < 8 || !TimeSpan.TryParseExact(name[..8], "hh\\-mm\\-ss", CultureInfo.InvariantCulture, out _))
        {
            return false;
        }
        return name.Length == 8
            || (name[8] == '-' && name[9..].All(char.IsDigit));
    }

    private string? ReadRunFile(RunRecord record, string fileName)
    {
        byte[]? bytes = ReadRunBytes(record, fileName);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    private byte[]? ReadRunBytes(RunRecord record, string fileName)
    {
        if (string.IsNullOrWhiteSpace(record.HistoryDirectory) || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }
        try
        {
            string dayDir = Path.Combine(_historyDir, record.StartTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            string runDir = ResolveWithin(dayDir, record.HistoryDirectory);
            string target = ResolveWithin(runDir, Path.GetFileName(fileName));
            if (!Directory.Exists(runDir) || !File.Exists(target))
            {
                return null;
            }
            return File.ReadAllBytes(target);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 读取历史文件失败：{ex.Message}");
            return null;
        }
    }

    private static string ResolveWithin(string root, string relative)
    {
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("历史文件路径越界");
        }
        return full;
    }

    private static bool IsDirectChild(string parent, string child)
    {
        string parentFull = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string childFull = Path.GetFullPath(child);
        string relative = childFull.StartsWith(parentFull, StringComparison.OrdinalIgnoreCase)
            ? childFull[parentFull.Length..]
            : "";
        return relative.Length > 0
            && !relative.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string FindFreeDirectoryName(string parent, string baseName)
    {
        string candidate = Path.Combine(parent, baseName);
        if (!Directory.Exists(candidate) && !File.Exists(candidate))
        {
            return baseName;
        }
        for (int index = 2; index < 1000; index++)
        {
            string name = $"{baseName}-{index}";
            candidate = Path.Combine(parent, name);
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return name;
            }
        }
        return $"{baseName}-{Guid.NewGuid():N}";
    }

    private static string SafeSegment(string? value, string fallback)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0)
        {
            return fallback;
        }
        char[] invalid = Path.GetInvalidFileNameChars();
        text = new string(text.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim().TrimEnd('.', ' ');
        if (text.Length == 0 || text is "." or "..")
        {
            return fallback;
        }
        if (text.EnumerateRunes().Count() > 80)
        {
            text = string.Concat(text.EnumerateRunes().Take(80)).TrimEnd('.', ' ');
        }
        string upper = text.ToUpperInvariant();
        if (upper is "CON" or "PRN" or "AUX" or "NUL"
            || (upper.Length == 4 && upper.StartsWith("COM", StringComparison.Ordinal) && upper[3] is >= '1' and <= '9')
            || (upper.Length == 4 && upper.StartsWith("LPT", StringComparison.Ordinal) && upper[3] is >= '1' and <= '9'))
        {
            return "_" + text;
        }
        return text;
    }

    private static int CountLines(string text) => text.Count(c => c == '\n') + (text.Length == 0 ? 0 : 1);

    private static void TryDeleteTemporaryDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 清理历史临时目录失败：{path}：{ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 清理旧历史源文件失败：{path}：{ex.Message}");
        }
    }
}
