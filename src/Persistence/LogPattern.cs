using System.Text;
using NexusPipeline.Utilities;

namespace NexusPipeline.Persistence;

/// <summary>日志路径格式解析：严格按用户给出的格式寻找日志文件，不做格式外的猜测。
/// 支持日期占位符（{YYYY-MM-DD} / {YYYYMMDD} 等 DateTime 格式串）与 * 通配（匹配任意字符，不含目录分隔符）。</summary>
internal static class LogPattern
{
    /// <summary>解析格式路径：返回当前应监控的文件；无匹配返回 null。
    /// 规则：已存在目录 → 目录内最新文件（旧配置兼容）；无占位符无通配 → 精确文件；含占位符 → 替换为当天日期后精确匹配；含 * → 目录内通配取最新修改。</summary>
    public static string? ResolveFile(string pattern)
    {
        return ResolveFiles(pattern)
            .OrderByDescending(path =>
            {
                try { return File.GetLastWriteTime(path); }
                catch { return DateTime.MinValue; }
            })
            .FirstOrDefault();
    }

    /// <summary>返回本次格式能匹配的全部候选文件，供一次 Attempt 开始时建立路径/FileId 快照。</summary>
    public static IReadOnlyList<string> ResolveFiles(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return Array.Empty<string>();
        }
        try
        {
            string path = pattern.Trim();
            if (Directory.Exists(path))
            {
                return Directory.GetFiles(path);
            }
            string? expanded = ExpandDateTokens(path);
            if (expanded is null)
            {
                Logger.Warn($"[日志格式] 路径格式含非法日期占位符，按无匹配处理：{path}");
                return Array.Empty<string>();
            }
            if (expanded.IndexOf('*') < 0)
            {
                return File.Exists(expanded) ? new[] { expanded } : Array.Empty<string>();
            }
            string dir = Path.GetDirectoryName(expanded) ?? "";
            string name = Path.GetFileName(expanded);
            return !Directory.Exists(dir)
                ? Array.Empty<string>()
                : Directory.GetFiles(dir, name);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[日志格式] 解析「{pattern}」异常：{ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>将 {格式串} 占位符替换为本地当前时间；占位符非法返回 null（整体视为无匹配）。</summary>
    private static string? ExpandDateTokens(string path)
    {
        var sb = new StringBuilder(path.Length + 8);
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '{')
            {
                int end = path.IndexOf('}', i + 1);
                if (end < 0)
                {
                    return null;
                }
                string token = path.Substring(i + 1, end - i - 1);
                if (string.IsNullOrWhiteSpace(token))
                {
                    return null;
                }
                string value;
                try
                {
                    // 用户直觉写法（YYYY=年、DD=日）归一化为 .NET 格式符（yyyy、dd），其余格式符原样支持
                    value = DateTime.Now.ToString(token.Replace('Y', 'y').Replace('D', 'd'));
                }
                catch (FormatException)
                {
                    return null;
                }
                sb.Append(value);
                i = end;
            }
            else
            {
                sb.Append(path[i]);
            }
        }
        return sb.ToString();
    }
}
