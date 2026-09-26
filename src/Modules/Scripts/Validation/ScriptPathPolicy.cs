using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
namespace NexusPipeline.Modules.Scripts.Validation;


/// <summary>约束体系：首次启动缺失时生成默认配置；绝对安全区间（内置默认值）静默生效；超安全值但入警告区间 → 启动警告；超警告区间或区间矛盾 → FATAL 拒绝启动。</summary>
internal static class ScriptPathPolicy
{

    /// <summary>
    /// 脚本实例路径校验（Web + CLI 共用）：
    /// 通用脚本——根目录/主程序/配置文件必须存在（主程序还需可执行），日志路径仅格式合规（不查存在性，支持日期占位符与通配符）；
    /// 专项脚本——仅校验根目录存在（主程序/配置/日志由当前插件 profile 解析）；
    /// 游戏配置（按启动方式分叉）——PC 客户端：游戏路径一律必填且必须为存在的可执行文件；安卓模拟器：ADB 地址必填且格式合法（主机:端口）。
    /// 返回错误信息或 null。
    /// </summary>
    public static string? CheckScriptPaths(ScriptInstance script, IPluginCapabilityResolver capabilities)
    {
        if (!string.IsNullOrWhiteSpace(script.ExecutionProviderId))
        {
            if (!string.IsNullOrWhiteSpace(script.PluginType)
                || !string.IsNullOrWhiteSpace(script.MainExe)
                || !string.IsNullOrWhiteSpace(script.Args)
                || !string.IsNullOrWhiteSpace(script.ConfigPath)
                || !string.IsNullOrWhiteSpace(script.LogPath)
                || script.JudgeScriptEnabled || script.HasKeywords())
                return "直驱实例不能同时声明外部脚本、专项插件或日志判定路径";
            if (!IsProviderConfigId(script.ExecutionProviderConfigId))
                return "直驱实例 provider 配置身份无效";
            if (string.IsNullOrWhiteSpace(script.RootPath) || !Directory.Exists(script.RootPath))
                return $"项目根目录不存在或不是文件夹：{script.RootPath}";
            if (script.LaunchGame || script.ForceCloseGame)
            {
                if (IsEmulator(script) ? !IsValidAdbAddress(script.GameExe) : !ExecutablePathRules.IsExecutable(script.GameExe))
                    return "直驱 Host 启动/关闭目标必须为有效的游戏可执行文件或模拟器 ADB 地址";
                if (IsEmulator(script) && script.LaunchGame && string.IsNullOrWhiteSpace(script.GameArgs))
                    return "直驱模拟器 Host 启动需要明确 am start 参数";
            }
            return null;
        }
        if (!string.IsNullOrWhiteSpace(script.ExecutionProviderConfigId))
            return "未声明执行 provider，不能保存 provider 配置身份";
        bool specialized = !string.IsNullOrWhiteSpace(script.PluginType);
        string root = script.RootPath.Trim();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return $"脚本根目录不存在或不是文件夹：{root}";
        }
        if (IsEmulator(script) && specialized
            && !capabilities.SupportsEmulator(script.PluginType))
        {
            return "该专项插件不支持安卓模拟器启动方式（请在 plugin.json 的 capabilities 中声明 emulator）";
        }
        if (!specialized)
        {
            if (!ExecutablePathRules.IsExecutable(script.MainExe))
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
        if (IsEmulator(script))
        {
            if (!IsValidAdbAddress(script.GameExe))
            {
                return $"模拟器ADB地址格式不正确（应为 主机:端口，如 127.0.0.1:16384）：{script.GameExe}";
            }
        }
        else if (!ExecutablePathRules.IsExecutable(script.GameExe))
        {
            return $"游戏路径必须为存在的可执行文件：{script.GameExe}";
        }
        return null;
    }

    internal static bool IsProviderConfigId(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
            or >= '0' and <= '9' or '-' or '_');

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

    private static bool IsEmulator(ScriptInstance script)
    {
        return string.Equals(script.GameMode, "emulator", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidAdbAddress(string value)
    {
        string normalized = value.Trim();
        int separator = normalized.LastIndexOf(':');
        if (separator <= 0 || separator == normalized.Length - 1 || normalized.IndexOf(':') != separator)
        {
            return false;
        }
        string host = normalized[..separator].Trim();
        return Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6
            && int.TryParse(normalized[(separator + 1)..], out int port)
            && port is >= 1 and <= 65535;
    }
}
