using System.Text;
using System.Text.Json;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;
using NexusPipeline.App.Abstractions;

namespace NexusPipeline.Services;

internal class HistoryService : IHistoryStore
{
    private static readonly object Sync = new();

    /// <summary>保存运行历史（精简）：.json 纯运行状态 + 按尝试分批 .log（{base}-{尝试号}.log）。</summary>
    public HistorySaveResult Save(RunRecord record, List<string> attemptLogs)
    {
        if (record.StartTime == DateTime.MinValue)
        {
            return new HistorySaveResult(record.Clone(), null);
        }
        RunRecord persisted = record.Clone();
        try
        {
            lock (Sync)
            {
                string dayDir = Path.Combine(AppPaths.HistoryDir, persisted.StartTime.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(dayDir);
                string baseName = persisted.StartTime.ToString("HH-mm-ss");
                string jsonPath = FindFreePath(dayDir, baseName, ".json");
                string jsonBase = Path.GetFileNameWithoutExtension(jsonPath);
                persisted.LogFile = Path.GetFileName(jsonPath);
                for (int i = 0; i < persisted.AttemptDetails.Count && i < attemptLogs.Count; i++)
                {
                    string attemptLogName = $"{jsonBase}-{persisted.AttemptDetails[i].Number}.log";
                    persisted.AttemptDetails[i].LogFile = attemptLogName;
                    string attemptLogText = string.IsNullOrWhiteSpace(attemptLogs[i])
                        ? "（未配置日志路径或未监控到脚本日志）" + Environment.NewLine
                        : attemptLogs[i];
                    File.WriteAllText(Path.Combine(dayDir, attemptLogName), attemptLogText, new UTF8Encoding(true));
                }
                // RunRecord JSON 是提交标记：尝试日志全部写完后才原子替换 JSON。
                JsonUtil.WriteAtomic(jsonPath, JsonSerializer.Serialize(persisted, JsonOpts.Indented));
            }
            return new HistorySaveResult(persisted.Clone(), null);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 保存运行历史失败：{ex.Message}");
            return new HistorySaveResult(persisted.Clone(), $"保存运行历史失败：{ex.Message}");
        }
    }

    /// <summary>读取某天的运行记录：直接扫描 .json 目录（起无 jsonl 索引）。</summary>
    private static List<RunRecord> ReadDayRecords(DateTime date)
    {
        var records = new List<RunRecord>();
        string dayDir = Path.Combine(AppPaths.HistoryDir, date.ToString("yyyy-MM-dd"));
        if (!Directory.Exists(dayDir))
        {
            return records;
        }
        foreach (string file in Directory.GetFiles(dayDir, "*.json"))
        {
            try
            {
                RunRecord? record = JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(file), JsonOpts.Default);
                if (record is not null)
                {
                    records.Add(record);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"历史记录文件解析失败已跳过（{file}）：{ex.Message}");
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

    private static string FindFreePath(string directory, string baseName, string extension)
    {
        string candidate = Path.Combine(directory, baseName + extension);
        if (!File.Exists(candidate))
        {
            return candidate;
        }
        for (int i = 1; i < 1000; i++)
        {
            candidate = Path.Combine(directory, $"{baseName}-{i}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
        return Path.Combine(directory, $"{baseName}-{Guid.NewGuid().ToString("N")[..6]}{extension}");
    }

    public List<RunRecord> Query(DateTime start, DateTime end, string? scriptId = null, string? queueId = null)
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
                    if (Matches(record, scriptId, queueId))
                    {
                        result.Add(record);
                    }
                }
            }
        }
        return result.OrderByDescending(record => record.StartTime).ToList();
    }

    private static bool Matches(RunRecord record, string? scriptId, string? queueId)
    {
        if (!string.IsNullOrWhiteSpace(scriptId) && record.ScriptInstanceId != scriptId)
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(queueId) && record.QueueId != queueId)
        {
            return false;
        }
        return true;
    }

    /// <summary>按 Id 查找历史记录：默认窗口取保留天数上限：此前固定 31 天，与保留上限
    /// （固定上限 180 天）不一致——超出窗口的记录点详情 404；显式传 days 时沿用原语义。</summary>
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

    /// <summary>读取某次尝试的脚本日志（.log 按尝试分批文件）。</summary>
    public (string LogText, int TotalLines)? ReadScriptLog(RunRecord record, int attemptNo)
    {
        RunAttempt? attempt = record.AttemptDetails.FirstOrDefault(a => a.Number == attemptNo);
        return attempt is null ? null : ReadDayFile(record, attempt.LogFile);
    }

    private static (string LogText, int TotalLines)? ReadDayFile(RunRecord record, string attemptLogFile)
    {
        if (string.IsNullOrWhiteSpace(attemptLogFile))
        {
            return null;
        }
        try
        {
            string dayDir = Path.Combine(AppPaths.HistoryDir, record.StartTime.ToString("yyyy-MM-dd"));
            string target = Path.Combine(dayDir, Path.GetFileName(attemptLogFile));
            if (!File.Exists(target))
            {
                return null;
            }
            string text = File.ReadAllText(target, Encoding.UTF8);
            int lines = text.Count(c => c == '\n') + (text.Length == 0 ? 0 : 1);
            return (text, lines);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 读取历史文件失败：{ex.Message}");
            return null;
        }
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

        // （P4）：与 Save 共享 Sync 锁——此前无锁时删除中的 dayDir 若被 Save 的 CreateDirectory 重建，
        // 递归删除会清掉刚写入的历史文件（历史丢失）。
        lock (Sync)
        {
        if (Directory.Exists(AppPaths.HistoryDir))
        {
            foreach (string directory in Directory.GetDirectories(AppPaths.HistoryDir))
            {
                string name = Path.GetFileName(directory);
                if (DateTime.TryParseExact(name, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime dirDate)
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

        foreach (string directory in new[] { AppPaths.OutputDir, AppPaths.LogDir })
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
}
