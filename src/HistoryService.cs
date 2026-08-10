using System.Text;
using System.Text.Json;

namespace NexusPipeline;

internal class HistoryService
{
    private static readonly object Sync = new();

    public void Save(RunRecord record, string scriptLog, string consoleLog = "")
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
                string logPath = Path.ChangeExtension(jsonPath, ".log");
                string consolePath = Path.ChangeExtension(jsonPath, ".console.log");
                record.LogFile = Path.GetFileName(jsonPath);
                File.WriteAllText(jsonPath, JsonSerializer.Serialize(record, JsonOpts.Indented), new UTF8Encoding(true));
                string logText = string.IsNullOrWhiteSpace(scriptLog)
                    ? "（未配置日志路径或未监控到脚本日志）" + Environment.NewLine
                    : scriptLog;
                File.WriteAllText(logPath, logText, new UTF8Encoding(true));
                string consoleText = string.IsNullOrWhiteSpace(consoleLog)
                    ? "（无控制台输出）" + Environment.NewLine
                    : consoleLog;
                File.WriteAllText(consolePath, consoleText, new UTF8Encoding(true));
                AppendIndex(record.StartTime, record);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 保存运行历史失败：{ex.Message}");
        }
    }

    private static string IndexPath(DateTime time)
    {
        return Path.Combine(AppPaths.HistoryDir, $"runs-{time:yyyy-MM-dd}.jsonl");
    }

    private static void AppendIndex(DateTime time, RunRecord record)
    {
        string index = IndexPath(time);
        File.AppendAllText(index, JsonSerializer.Serialize(record, JsonOpts.Default) + Environment.NewLine, new UTF8Encoding(false));
    }

    /// <summary>读取某天的运行记录：优先顺序索引 runs-*.jsonl；旧数据无索引时扫描 .json 目录并重建索引。</summary>
    private static List<RunRecord> ReadDayIndex(DateTime date)
    {
        string index = IndexPath(date);
        if (File.Exists(index))
        {
            var records = new List<RunRecord>();
            foreach (string line in File.ReadAllLines(index))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                try
                {
                    RunRecord? record = JsonSerializer.Deserialize<RunRecord>(line, JsonOpts.Default);
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

        string dayDir = Path.Combine(AppPaths.HistoryDir, date.ToString("yyyy-MM-dd"));
        var rebuilt = new List<RunRecord>();
        if (Directory.Exists(dayDir))
        {
            foreach (string file in Directory.GetFiles(dayDir, "*.json"))
            {
                try
                {
                    RunRecord? record = JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(file), JsonOpts.Default);
                    if (record is not null)
                    {
                        rebuilt.Add(record);
                    }
                }
                catch
                {
                }
            }
            if (rebuilt.Count > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(index) ?? AppPaths.HistoryDir);
                File.WriteAllLines(index, rebuilt.Select(record => JsonSerializer.Serialize(record, JsonOpts.Default)), new UTF8Encoding(false));
            }
        }
        return rebuilt;
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
                foreach (RunRecord record in ReadDayIndex(date))
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
                foreach (RunRecord record in ReadDayIndex(date))
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

    public (string LogText, int TotalLines)? ReadScriptLog(RunRecord record)
    {
        return ReadDayFile(record, ".log");
    }

    /// <summary>读取本次运行完整控制台输出（.console.log 三件套）。</summary>
    public (string LogText, int TotalLines)? ReadConsoleLog(RunRecord record)
    {
        return ReadDayFile(record, ".console.log");
    }

    private static (string LogText, int TotalLines)? ReadDayFile(RunRecord record, string extension)
    {
        if (string.IsNullOrWhiteSpace(record.LogFile))
        {
            return null;
        }
        try
        {
            string dayDir = Path.Combine(AppPaths.HistoryDir, record.StartTime.ToString("yyyy-MM-dd"));
            string logPath = Path.Combine(dayDir, Path.GetFileName(record.LogFile));
            string target = Path.ChangeExtension(logPath, extension);
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
            foreach (string file in Directory.GetFiles(AppPaths.HistoryDir, "runs-*.jsonl"))
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
