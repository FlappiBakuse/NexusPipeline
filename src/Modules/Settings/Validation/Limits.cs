using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Serialization;
namespace NexusPipeline.Modules.Settings.Validation;


/// <summary>约束体系：首次启动缺失时生成默认配置；绝对安全区间（内置默认值）静默生效；超安全值但入警告区间 → 启动警告；超警告区间或区间矛盾 → FATAL 拒绝启动。</summary>
internal static class Limits
{
    public static AppLimits Current { get; private set; } = new();

    public static List<string> Warnings { get; } = new();

    public static List<string> Fatals { get; } = new();

    public static void Load()
    {
        var limits = new AppLimits();
        var warnings = new List<string>();
        var fatals = new List<string>();
        if (File.Exists(AppPaths.LimitsPath))
        {
            try
            {
                string text = File.ReadAllText(AppPaths.LimitsPath);
                AppLimits? parsed = JsonSerializer.Deserialize<AppLimits>(text, JsonOpts.Default);
                if (parsed is not null)
                {
                    limits = parsed;
                    TryRemoveRetiredConfigFields(text, warnings);
                }
            }
            catch (Exception ex)
            {
                fatals.Add($"limits.json 解析失败：{ex.Message}");
            }
        }
        else
        {
            TryCreateDefaultConfig(limits, warnings);
        }

        CheckCount(limits.MaxScripts, 50, 999, "MaxScripts（脚本实例上限）", warnings, fatals);
        CheckCount(limits.MaxUsersPerScript, 50, 999, "MaxUsersPerScript（每脚本用户上限）", warnings, fatals);
        CheckCount(limits.MaxUsers, 50, 999, "MaxUsers（全局用户上限）", warnings, fatals);
        CheckCount(limits.MaxQueues, 50, 999, "MaxQueues（调度队列上限）", warnings, fatals);
        CheckCount(limits.MaxQueueTotalUsers, 50, 999, "MaxQueueTotalUsers（队列任务总用户上限）", warnings, fatals);
        CheckCount(limits.MaxTimeSetsPerQueue, 10, 999, "MaxTimeSetsPerQueue（每队列定时上限）", warnings, fatals);
        CheckSentinelUpper(limits.MaxRunDays, -1, 365, 3650, "MaxRunDays（运行天数上限）", warnings, fatals);
        CheckSentinelUpper(limits.MaxSuccessfulRunsPerDay, -1, 10, 99, "MaxSuccessfulRunsPerDay（每日成功运行次数上限）", warnings, fatals);
        CheckRange(limits.MinAttempts, limits.MaxAttempts, 1, 10, 99, "尝试次数（Min/MaxAttempts）", warnings, fatals);
        CheckRange(limits.MinStallMinutes, limits.MaxStallMinutes, 1, 60, 1440, "日志无更新超时（Min/MaxStallMinutes）", warnings, fatals);
        CheckRange(limits.MinTotalMinutes, limits.MaxTotalMinutes, 5, 720, 10080, "运行总时间超时（Min/MaxTotalMinutes）", warnings, fatals, allowedMin: 5);

        Current = limits;
        Warnings.Clear();
        Warnings.AddRange(warnings);
        Fatals.Clear();
        Fatals.AddRange(fatals);
    }

    private static void TryCreateDefaultConfig(AppLimits limits, List<string> warnings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ConfigDir);
            JsonUtil.WriteAtomic(AppPaths.LimitsPath, JsonSerializer.Serialize(limits, JsonOpts.Indented));
        }
        catch (Exception ex)
        {
            warnings.Add($"[警告] 默认 limits.json 写入失败，将继续使用内置约束值：{ex.Message}");
        }
    }

    private static void TryRemoveRetiredConfigFields(string text, List<string> warnings)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root)
            {
                return;
            }

            string[] retiredFields = root
                .Select(property => property.Key)
                .Where(key => key.Equals("MaxScriptNameBytes", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("MaxQueueNameBytes", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("MaxHistoryRetentionDays", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (retiredFields.Length == 0)
            {
                return;
            }

            foreach (string field in retiredFields)
            {
                root.Remove(field);
            }
            JsonUtil.WriteAtomic(AppPaths.LimitsPath, root.ToJsonString(JsonOpts.Indented));
        }
        catch (Exception ex)
        {
            warnings.Add($"[警告] limits.json 已移除字段清理失败，将继续使用固定值：{ex.Message}");
        }
    }

    private static void CheckCount(int value, int safeMax, int warnMax, string label, List<string> warnings, List<string> fatals)
    {
        if (value < 1 || value > warnMax)
        {
            fatals.Add($"约束配置 [{label}={value}] 超出警告区间（允许 1-{warnMax}），禁止启动");
            return;
        }
        if (value > safeMax)
        {
            warnings.Add($"[警告] 约束配置 [{label}={value}] 超出绝对安全上限 {safeMax}（允许 1-{warnMax}），已按配置生效，请注意数据规模");
        }
    }

    private static void CheckRange(int min, int max, int safeMin, int safeMax, int warnMax, string label, List<string> warnings, List<string> fatals, int allowedMin = 1)
    {
        if (min > max)
        {
            fatals.Add($"约束配置 [{label}] 区间矛盾（Min={min} 大于 Max={max}），禁止启动");
            return;
        }
        CheckEnd(min, safeMin, safeMax, warnMax, $"{label} Min", warnings, fatals, allowedMin);
        CheckEnd(max, safeMin, safeMax, warnMax, $"{label} Max", warnings, fatals, allowedMin);
    }

    private static void CheckSentinelUpper(int value, int allowedMin, int safeMax, int allowedMax, string label, List<string> warnings, List<string> fatals)
    {
        if (value < allowedMin || value > allowedMax)
        {
            fatals.Add($"约束配置 [{label}={value}] 超出警告区间（允许 {allowedMin}-{allowedMax}），禁止启动");
            return;
        }
        if (value > safeMax)
        {
            warnings.Add($"[警告] 约束配置 [{label}={value}] 超出绝对安全上限 {safeMax}（允许 {allowedMin}-{allowedMax}），已按配置生效，请注意运行规模");
        }
    }

    private static void CheckEnd(int value, int safeMin, int safeMax, int warnMax, string label, List<string> warnings, List<string> fatals, int allowedMin)
    {
        if (value < allowedMin || value > warnMax)
        {
            fatals.Add($"约束配置 [{label}={value}] 超出警告区间（允许 {allowedMin}-{warnMax}），禁止启动");
            return;
        }
        if (value > safeMax || value < safeMin)
        {
            warnings.Add($"[警告] 约束配置 [{label}={value}] 超出绝对安全区间（允许 {safeMin}-{safeMax}，警告区间至 {warnMax}），已按配置生效");
        }
    }

    /* ---------- 数据填写校验（Web + CLI 共用） ---------- */

    public static string? CheckScriptCount(int count)
    {
        return count >= Current.MaxScripts ? $"脚本实例数量已达上限（{count}/{Current.MaxScripts}）" : null;
    }

    public static string? CheckQueueCount(int count)
    {
        return count >= Current.MaxQueues ? $"调度队列数量已达上限（{count}/{Current.MaxQueues}）" : null;
    }

    public static string? CheckUserCount(int count)
    {
        return count >= Current.MaxUsersPerScript ? $"该脚本的用户数量已达上限（{count}/{Current.MaxUsersPerScript}）" : null;
    }

    public static string? CheckGlobalUserCount(int count)
    {
        return count >= Current.MaxUsers ? $"全局用户数量已达上限（{count}/{Current.MaxUsers}）" : null;
    }

    public static string? CheckNameBytes(string name, int maxBytes, string label)
    {
        return Encoding.UTF8.GetByteCount(name) > maxBytes ? $"{label}最多 {maxBytes} 字节" : null;
    }

    public static string? CheckAttempts(int value)
    {
        return value >= Current.MinAttempts && value <= Current.MaxAttempts ? null : $"最大尝试次数须在 {Current.MinAttempts}-{Current.MaxAttempts} 之间";
    }

    public static string? CheckStallMinutes(int value)
    {
        if (value == -1)
        {
            return null;
        }
        return value >= Current.MinStallMinutes && value <= Current.MaxStallMinutes ? null : $"日志无更新超时须在 {Current.MinStallMinutes}-{Current.MaxStallMinutes} 分钟之间（-1 为不超时）";
    }

    public static string? CheckTotalMinutes(int value)
    {
        if (value == -1)
        {
            return null;
        }
        return value >= Current.MinTotalMinutes && value <= Current.MaxTotalMinutes ? null : $"运行总时间超时须在 {Current.MinTotalMinutes}-{Current.MaxTotalMinutes} 分钟之间（-1 为不超时）";
    }

    public static string? CheckRunDays(int value)
    {
        if (value < -1)
        {
            return "运行天数只能为 -1（永久）、0（停止）或当前上限以内的正整数";
        }
        if (value > 0 && (Current.MaxRunDays < 1 || value > Current.MaxRunDays))
        {
            return $"运行天数须在 -1（永久）、0（停止）或 1-{Current.MaxRunDays} 之间";
        }
        return null;
    }

    public static string? CheckMaxSuccessfulRunsPerDay(int value)
    {
        if (value == 0 || value < -1)
        {
            return "最多成功运行次数只能为 -1（不限制）或当前上限以内的正整数，不能为 0";
        }
        if (value > 0 && (Current.MaxSuccessfulRunsPerDay < 1 || value > Current.MaxSuccessfulRunsPerDay))
        {
            return $"最多成功运行次数须为 -1（不限制）或 1-{Current.MaxSuccessfulRunsPerDay}";
        }
        return null;
    }

    /// <summary>
    /// 校验脚本超时关系：日志无更新上限为 -1 即定义为长时脚本；长时脚本的运行总时间上限可为 -1 或有效的正数；普通脚本的运行总时间上限不能为 -1。
    /// </summary>
    public static string? CheckScriptTimeouts(int stallMinutes, int totalMinutes)
    {
        if (stallMinutes == -1)
        {
            return CheckTotalMinutes(totalMinutes);
        }

        if (totalMinutes == -1)
        {
            return "日志无更新上限未填 -1 时，运行总时间上限不能填 -1";
        }

        return CheckStallMinutes(stallMinutes) ?? CheckTotalMinutes(totalMinutes);
    }

    public static string? CheckRetentionDays(int value)
    {
        return value >= 1 && value <= AppFixedLimits.HistoryRetentionDaysMax ? null : $"历史保留天数须在 1-{AppFixedLimits.HistoryRetentionDaysMax} 天之间";
    }

    public static string? CheckTimeSets(int count)
    {
        return count > Current.MaxTimeSetsPerQueue ? $"定时列表已达上限（{count}/{Current.MaxTimeSetsPerQueue}）" : null;
    }

    public static string? CheckQueueTotalUsers(int total)
    {
        return total > Current.MaxQueueTotalUsers ? $"任务列表的启用用户总数已达上限（{total}/{Current.MaxQueueTotalUsers}）" : null;
    }
}
