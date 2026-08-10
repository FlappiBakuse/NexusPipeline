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
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }
        try
        {
            string path = pattern.Trim();
            if (Directory.Exists(path))
            {
                return Directory.GetFiles(path)
                    .OrderByDescending(File.GetLastWriteTime)
                    .FirstOrDefault();
            }
            string? expanded = ExpandDateTokens(path);
            if (expanded is null)
            {
                Logger.Warn($"[日志格式] 路径格式含非法日期占位符，按无匹配处理：{path}");
                return null;
            }
            if (expanded.IndexOf('*') < 0)
            {
                return File.Exists(expanded) ? expanded : null;
            }
            string dir = Path.GetDirectoryName(expanded) ?? "";
            string name = Path.GetFileName(expanded);
            if (!Directory.Exists(dir))
            {
                return null;
            }
            string[] matches = Directory.GetFiles(dir, name);
            return matches.OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[日志格式] 解析「{pattern}」异常：{ex.Message}");
            return null;
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
