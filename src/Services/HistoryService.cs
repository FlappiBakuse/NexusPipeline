using System.Text;
using System.Text.Json;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

internal class HistoryService
{
    private static readonly object Sync = new();

    /// <summary>保存运行历史（v0.5.3 精简）：.json 纯运行状态 + 按尝试分批 .log（{base}-{尝试号}.log）。</summary>
    public void Save(RunRecord record, List<string> attemptLogs)
    {
        if (record.StartTime == DateTime.MinValue)
        {
            return;
        }
        try
        {
            lock (Sync)
            {
                string dayDir = Path.Combine(AppPaths.HistoryDir, record.StartTime.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(dayDir);
                string baseName = record.StartTime.ToString("HH-mm-ss");
                string jsonPath = FindFreePath(dayDir, baseName, ".json");
                string jsonBase = Path.GetFileNameWithoutExtension(jsonPath);
                record.LogFile = Path.GetFileName(jsonPath);
                for (int i = 0; i < record.AttemptDetails.Count && i < attemptLogs.Count; i++)
                {
                    string attemptLogName = $"{jsonBase}-{record.AttemptDetails[i].Number}.log";
                    record.AttemptDetails[i].LogFile = attemptLogName;
                    string attemptLogText = string.IsNullOrWhiteSpace(attemptLogs[i])
                        ? "（未配置日志路径或未监控到脚本日志）" + Environment.NewLine
                        : attemptLogs[i];
                    File.WriteAllText(Path.Combine(dayDir, attemptLogName), attemptLogText, new UTF8Encoding(true));
                }
                File.WriteAllText(jsonPath, JsonSerializer.Serialize(record, JsonOpts.Indented), new UTF8Encoding(true));
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 保存运行历史失败：{ex.Message}");
        }
    }

    /// <summary>读取某天的运行记录：直接扫描 .json 目录（v0.5.3 起无 jsonl 索引）。</summary>
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
            catch
            {
            }
        }
        return records;
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

    public RunRecord? FindById(string id, int days = 31)
    {
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

    /// <summary>读取某次尝试的脚本日志（.log 按尝试分批文件，v0.5.3+）。</summary>
    public (string LogText, int TotalLines)? ReadScriptLog(RunRecord record, int attemptNo)
    {
        RunAttempt? attempt = record.AttemptDetails.FirstOrDefault(a => a.Number == attemptNo);
        return attempt is null ? null : ReadDayFile(record, attempt.LogFile);
    }

    /// <summary>读取运行内全部尝试的日志（兼容旧数据：无按尝试文件时回退读取旧 .log 单文件）。</summary>
    public (string LogText, int TotalLines)? ReadLegacyScriptLog(RunRecord record)
    {
        return ReadDayFile(record, ".log");
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
        Logger.Info($"清理完成，共删除 {removed} 个过期项。");
    }
}
