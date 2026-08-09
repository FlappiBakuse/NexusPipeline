using System.Text;
using System.Text.Json;

namespace NexusPipeline;

/// <summary>数据填写约束配置（config/limits.json，启动加载，程序只读不写）。字段为 PascalCase。</summary>
internal class AppLimits
{
    public int MaxScripts { get; set; } = 25;

    public int MaxUsersPerScript { get; set; } = 10;

    public int MaxQueues { get; set; } = 10;

    public int MaxTimeSetsPerQueue { get; set; } = 10;

    public int MaxQueueTotalUsers { get; set; } = 50;

    public int MaxScriptNameBytes { get; set; } = 128;

    public int MaxQueueNameBytes { get; set; } = 128;

    public int MinAttempts { get; set; } = 1;

    public int MaxAttempts { get; set; } = 10;

    public int MinStallMinutes { get; set; } = 1;

    public int MaxStallMinutes { get; set; } = 60;

    public int MinTotalMinutes { get; set; } = 5;

    public int MaxTotalMinutes { get; set; } = 720;
}

/// <summary>约束体系：绝对安全区间（内置默认值）静默生效；超安全值但入警告区间 → 启动警告；超警告区间或区间矛盾 → FATAL 拒绝启动。</summary>
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
                AppLimits? parsed = JsonSerializer.Deserialize<AppLimits>(File.ReadAllText(AppPaths.LimitsPath), JsonOpts.Default);
                if (parsed is not null)
                {
                    limits = parsed;
                }
            }
            catch (Exception ex)
            {
                fatals.Add($"limits.json 解析失败：{ex.Message}");
            }
        }

        CheckCount(limits.MaxScripts, 25, 50, "MaxScripts（脚本实例上限）", warnings, fatals);
        CheckCount(limits.MaxUsersPerScript, 10, 20, "MaxUsersPerScript（每脚本用户上限）", warnings, fatals);
        CheckCount(limits.MaxQueues, 10, 50, "MaxQueues（调度队列上限）", warnings, fatals);
        CheckCount(limits.MaxTimeSetsPerQueue, 10, 20, "MaxTimeSetsPerQueue（每队列定时上限）", warnings, fatals);
        CheckCount(limits.MaxQueueTotalUsers, 50, 150, "MaxQueueTotalUsers（队列任务总用户上限）", warnings, fatals);
        CheckCount(limits.MaxScriptNameBytes, 128, 128, "MaxScriptNameBytes（脚本名称字节上限）", warnings, fatals);
        CheckCount(limits.MaxQueueNameBytes, 128, 128, "MaxQueueNameBytes（队列名称字节上限）", warnings, fatals);
        CheckRange(limits.MinAttempts, limits.MaxAttempts, 1, 10, 30, "尝试次数（Min/MaxAttempts）", warnings, fatals);
        CheckRange(limits.MinStallMinutes, limits.MaxStallMinutes, 1, 60, 480, "日志无更新超时（Min/MaxStallMinutes）", warnings, fatals);
        CheckRange(limits.MinTotalMinutes, limits.MaxTotalMinutes, 5, 720, 2880, "运行总时间超时（Min/MaxTotalMinutes）", warnings, fatals);

        Current = limits;
        Warnings.Clear();
        Warnings.AddRange(warnings);
        Fatals.Clear();
        Fatals.AddRange(fatals);
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

    private static void CheckRange(int min, int max, int safeMin, int safeMax, int warnMax, string label, List<string> warnings, List<string> fatals)
    {
        if (min > max)
        {
            fatals.Add($"约束配置 [{label}] 区间矛盾（Min={min} 大于 Max={max}），禁止启动");
            return;
        }
        CheckEnd(min, safeMin, safeMax, warnMax, $"{label} Min", warnings, fatals);
        CheckEnd(max, safeMin, safeMax, warnMax, $"{label} Max", warnings, fatals);
    }

    private static void CheckEnd(int value, int safeMin, int safeMax, int warnMax, string label, List<string> warnings, List<string> fatals)
    {
        if (value < 1 || value > warnMax)
        {
            fatals.Add($"约束配置 [{label}={value}] 超出警告区间（允许 {safeMin}-{warnMax}），禁止启动");
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
        return value >= Current.MinStallMinutes && value <= Current.MaxStallMinutes ? null : $"日志无更新超时须在 {Current.MinStallMinutes}-{Current.MaxStallMinutes} 分钟之间";
    }

    public static string? CheckTotalMinutes(int value)
    {
        return value >= Current.MinTotalMinutes && value <= Current.MaxTotalMinutes ? null : $"运行总时间超时须在 {Current.MinTotalMinutes}-{Current.MaxTotalMinutes} 分钟之间";
    }

    /// <summary>
    /// 脚本实例路径校验（Web + CLI 共用）：
    /// 通用脚本——根目录/主程序/配置文件必须存在（主程序还需可执行），日志路径仅格式合规（不查存在性，支持日期占位符与通配符）；
    /// 专项脚本——仅校验根目录存在（主程序/配置/日志由插件固化，不做存在性校验）；
    /// 游戏路径一律必填且必须为存在的可执行文件（运行前启动游戏、运行后强制关闭游戏均与填写解绑）。返回错误信息或 null。
    /// </summary>
    public static string? CheckScriptPaths(ScriptInstance script)
    {
        bool specialized = !string.IsNullOrWhiteSpace(script.PluginType);
        string root = script.RootPath.Trim();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return $"脚本根目录不存在或不是文件夹：{root}";
        }
        if (!specialized)
        {
            if (!TextRules.IsExecutable(script.MainExe))
            {
                return $"脚本主程序路径不存在或不是可执行文件：{script.MainExe}";
            }
            string config = script.ConfigPath.Trim();
            if (string.IsNullOrWhiteSpace(config) || (!File.Exists(config) && !Directory.Exists(config)))
            {
                return $"配置文件路径/文件夹不存在：{config}";
            }
            if (!IsLogPathPlausible(script.LogPath))
            {
                return $"日志路径格式不合法（不允许包含 引号/尖括号/竖线/问号）：{script.LogPath}";
            }
        }
        if (!TextRules.IsExecutable(script.GameExe))
        {
            return $"游戏路径必须为存在的可执行文件：{script.GameExe}";
        }
        return null;
    }

    /// <summary>日志路径为「路径格式」：允许日期占位符与 * 通配，禁止其余非法字符（不要求文件存在）。</summary>
    private static bool IsLogPathPlausible(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        return path.IndexOfAny(new[] { '"', '<', '>', '|', '?' }) < 0;
    }

    public static string? CheckTimeSets(int count)
    {
        return count > Current.MaxTimeSetsPerQueue ? $"定时列表已达上限（{count}/{Current.MaxTimeSetsPerQueue}）" : null;
    }

    public static string? CheckQueueTotalUsers(int total)
    {
        return total > Current.MaxQueueTotalUsers ? $"任务列表的启用用户总数已达上限（{total}/{Current.MaxQueueTotalUsers}）" : null;
    }

    /// <summary>队列任务的启用用户总数：各任务引用脚本的启用用户数之和，每个任务至少计 1。</summary>
    public static int QueueTotalUsers(RuntimeContext ctx, DispatchQueue queue)
    {
        return queue.Tasks.Sum(task =>
        {
            ScriptInstance? script = ctx.FindScript(task.ScriptInstanceId);
            if (script is null)
            {
                return 1;
            }
            int enabled = script.Users.Count(user => user.Enabled);
            return enabled < 1 ? 1 : enabled;
        });
    }
}
