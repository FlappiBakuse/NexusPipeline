using System.Text;
using System.Text.Json;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;
using NexusPipeline.Extensibility;
using NexusPipeline.App.Abstractions;

namespace NexusPipeline.Services;

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
        CheckCount(limits.MaxUsers, 50, 150, "MaxUsers（全局用户上限）", warnings, fatals);
        CheckCount(limits.MaxQueues, 10, 50, "MaxQueues（调度队列上限）", warnings, fatals);
        CheckCount(limits.MaxTimeSetsPerQueue, 10, 20, "MaxTimeSetsPerQueue（每队列定时上限）", warnings, fatals);
        CheckCount(limits.MaxQueueTotalUsers, 50, 150, "MaxQueueTotalUsers（队列任务总用户上限）", warnings, fatals);
        CheckCount(limits.MaxScriptNameBytes, 128, 128, "MaxScriptNameBytes（脚本名称字节上限）", warnings, fatals);
        CheckCount(limits.MaxQueueNameBytes, 128, 128, "MaxQueueNameBytes（队列名称字节上限）", warnings, fatals);
        CheckRange(limits.MinAttempts, limits.MaxAttempts, 1, 10, 30, "尝试次数（Min/MaxAttempts）", warnings, fatals);
        CheckRange(limits.MinStallMinutes, limits.MaxStallMinutes, 1, 60, 480, "日志无更新超时（Min/MaxStallMinutes）", warnings, fatals);
        CheckRange(limits.MinTotalMinutes, limits.MaxTotalMinutes, 5, 720, 2880, "运行总时间超时（Min/MaxTotalMinutes）", warnings, fatals);
        CheckCount(limits.MaxHistoryRetentionDays, 180, 365, "MaxHistoryRetentionDays（历史保留天数上限）", warnings, fatals);

        Current = limits;
        Warnings.Clear();
        Warnings.AddRange(warnings);
        Fatals.Clear();
        Fatals.AddRange(fatals);
        // v0.6.6+：同步历史保留天数上限到 ConfigStore（settings Normalize 使用，消除硬编码 180）。
        ConfigStore.ApplyMaxHistoryRetentionDays(limits.MaxHistoryRetentionDays);
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

    /// <summary>
    /// 超时成对校验（v0.7.0 长时脚本）：-1（不超时）必须成对出现——任一为 -1 而另一为正常值 → 拒绝（避免半长时语义歧义）；
    /// 均正常时回退各自区间校验。
    /// </summary>
    public static string? CheckScriptTimeouts(int stallMinutes, int totalMinutes)
    {
        if (stallMinutes == -1 && totalMinutes == -1)
        {
            return null;
        }
        if (stallMinutes == -1 || totalMinutes == -1)
        {
            return "长时脚本需将「日志无更新超时」与「运行总时间超时」都设为 -1（-1 = 不超时）";
        }
        return CheckStallMinutes(stallMinutes) ?? CheckTotalMinutes(totalMinutes);
    }

    /// <summary>
    /// 队列长时/普通混排校验（v0.7.0）：队列链式串行执行，长时脚本（两个超时均为 -1）会无限阻塞后续任务——
    /// 长时脚本实例不能与普通脚本实例编排进同一队列。任务不足两项或全部同类时通过。
    /// </summary>
    public static string? CheckQueueMix(IEnumerable<ScriptInstance> scripts, DispatchQueue queue)
    {
        List<ScriptInstance> tasks = queue.Tasks
            .Select(task => scripts.FirstOrDefault(script => script.Id == task.ScriptInstanceId))
            .Where(script => script is not null)
            .Cast<ScriptInstance>()
            .ToList();
        if (tasks.Count < 2)
        {
            return null;
        }
        bool hasLong = tasks.Any(script => script.IsLongRunning);
        bool hasNormal = tasks.Any(script => !script.IsLongRunning);
        if (hasLong && hasNormal)
        {
            return "队列不能混合编排长时脚本（两个超时均为 -1）与普通脚本实例，请分开建立队列";
        }
        return null;
    }

    public static string? CheckRetentionDays(int value)
    {
        return value >= 1 && value <= Current.MaxHistoryRetentionDays ? null : $"历史保留天数须在 1-{Current.MaxHistoryRetentionDays} 天之间";
    }

    /// <summary>
    /// 脚本实例路径校验（Web + CLI 共用）：
    /// 通用脚本——根目录/主程序/配置文件必须存在（主程序还需可执行），日志路径仅格式合规（不查存在性，支持日期占位符与通配符）；
    /// 专项脚本——仅校验根目录存在（主程序/配置/日志由插件固化，不做存在性校验）；
    /// 游戏配置（v0.7.0+ 按启动方式分叉）——PC 客户端：游戏路径一律必填且必须为存在的可执行文件；安卓模拟器：ADB 地址必填且格式合法（主机:端口）。
    /// 返回错误信息或 null。
    /// </summary>
    public static string? CheckScriptPaths(ScriptInstance script, IPluginCapabilityResolver capabilities)
    {
        bool specialized = !string.IsNullOrWhiteSpace(script.PluginType);
        string root = script.RootPath.Trim();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return $"脚本根目录不存在或不是文件夹：{root}";
        }
        if (EmulatorSupport.IsEmulator(script) && specialized
            && !capabilities.SupportsEmulator(script.PluginType))
        {
            return "该专项插件不支持安卓模拟器启动方式（专用插件需在 plugin.json 声明 supportsEmulator）";
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
            string? managedPathError = CheckManagedPathOverlap(config);
            if (managedPathError is not null)
            {
                return managedPathError;
            }
            if (!IsLogPathPlausible(script.LogPath))
            {
                return $"日志路径格式不合法（不允许包含 引号/尖括号/竖线/问号）：{script.LogPath}";
            }
        }
        if (EmulatorSupport.IsEmulator(script))
        {
            if (!EmulatorSupport.IsValidAdbAddress(script.GameExe))
            {
                return $"模拟器ADB地址格式不正确（应为 主机:端口，如 127.0.0.1:16384）：{script.GameExe}";
            }
        }
        else if (!TextRules.IsExecutable(script.GameExe))
        {
            return $"游戏路径必须为存在的可执行文件：{script.GameExe}";
        }
        return null;
    }

    /// <summary>拒绝配置路径与宿主自管目录重叠，避免添加用户/自动镜像递归复制或删除宿主运行数据。</summary>
    private static string? CheckManagedPathOverlap(string configPath)
    {
        string[] managed =
        {
            AppPaths.ConfigDir,
            AppPaths.DataDir,
            AppPaths.HistoryDir,
            AppPaths.OutputDir,
            AppPaths.LogDir,
            AppPaths.WwwRootDir,
            AppPaths.PluginsDir,
        };
        try
        {
            string candidate = Path.GetFullPath(configPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string appRoot = Path.GetFullPath(AppPaths.AppRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(candidate, appRoot, StringComparison.OrdinalIgnoreCase))
            {
                return "配置路径不能指向 NexusPipeline 程序根目录";
            }
            foreach (string path in managed)
            {
                string managedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (IsSameOrWithin(candidate, managedPath) || IsSameOrWithin(managedPath, candidate))
                {
                    return $"配置路径不能与 NexusPipeline 自管目录重叠：{managedPath}";
                }
            }
        }
        catch (Exception ex)
        {
            return $"配置路径无法完成安全校验：{ex.Message}";
        }
        return null;
    }

    private static bool IsSameOrWithin(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        string relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith("../", StringComparison.Ordinal);
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

    /// <summary>队列任务的启用绑定总数：各任务引用脚本的启用绑定数之和，每个任务至少计 1。</summary>
    public static int QueueTotalUsers(RuntimeContext ctx, DispatchQueue queue)
    {
        return queue.Tasks.Sum(task =>
        {
            ScriptInstance? script = ctx.FindScript(task.ScriptInstanceId);
            if (script is null)
            {
                return 1;
            }
            int enabled = ctx.Users.Count > 0
                ? ctx.Users.Sum(user => user.Bindings.Count(binding =>
                    binding.Participates && string.Equals(binding.ScriptInstanceId, script.Id, StringComparison.Ordinal)))
                : script.Users.Count(user => user.Enabled);
            return enabled < 1 ? 1 : enabled;
        });
    }
}
