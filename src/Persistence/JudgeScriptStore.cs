using System.Text;
using NexusPipeline.Utilities;

namespace NexusPipeline.Persistence;

/// <summary>
/// 用户拥有的通用判断脚本资产仓储。
/// 文件名只由脚本实例 ID 与语言决定，调用方不能通过配置内容注入任意路径。
/// </summary>
internal sealed class JudgeScriptStore
{
    private readonly string _root;

    public JudgeScriptStore(string root)
    {
        _root = Path.GetFullPath(root);
    }

    public string Root => _root;

    public string GetPath(string scriptId, string language)
    {
        if (!IsSafeScriptId(scriptId))
        {
            throw new ArgumentException("判断脚本文件名中的脚本 ID 不安全", nameof(scriptId));
        }
        return Path.Combine(_root, scriptId + Extension(language));
    }

    public bool Exists(string scriptId, string language)
    {
        return File.Exists(GetPath(scriptId, language));
    }

    public string? Load(string scriptId, string language)
    {
        string path = GetPath(scriptId, language);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[判断脚本] 读取失败（{path}）：{ex.Message}");
            return null;
        }
    }

    public void SaveAtomic(string scriptId, string language, string content)
    {
        string path = GetPath(scriptId, language);
        Directory.CreateDirectory(_root);
        string temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // 临时文件保留时由后续启动清理/人工恢复处理，不覆盖原始异常。
            }
        }
    }

    public void Delete(string scriptId, string language)
    {
        string path = GetPath(scriptId, language);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>把旧语言或已删除实例的源码移入隔离区，保留人工恢复机会。</summary>
    public string? MoveToOrphaned(string scriptId, string language)
    {
        string source = GetPath(scriptId, language);
        if (!File.Exists(source))
        {
            return null;
        }
        string orphanDir = Path.Combine(_root, "orphaned", $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(orphanDir);
        string destination = Path.Combine(orphanDir, Path.GetFileName(source));
        File.Move(source, destination);
        return destination;
    }

    /// <summary>
    /// 保存脚本清单成功后隔离目录根下没有被清单引用的源码文件。
    /// 启动加载阶段不会删除源码，避免误删用户判断脚本。
    /// </summary>
    public void QuarantineUnreferenced(IReadOnlySet<string> referencedPaths)
    {
        if (!Directory.Exists(_root))
        {
            return;
        }
        foreach (string file in Directory.GetFiles(_root, "*", SearchOption.TopDirectoryOnly))
        {
            string extension = Path.GetExtension(file);
            if (!extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".py", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string fullPath = Path.GetFullPath(file);
            if (referencedPaths.Contains(fullPath))
            {
                continue;
            }
            try
            {
                string orphanDir = Path.Combine(_root, "orphaned", $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(orphanDir);
                File.Move(file, Path.Combine(orphanDir, Path.GetFileName(file)));
                Logger.Info($"未引用判断脚本已移入隔离区：{file}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[判断脚本] 隔离未引用源码失败（保留原文件）：{file}（{ex.Message}）");
            }
        }
    }

    public static string NormalizeLanguage(string? language)
    {
        return string.Equals(language?.Trim(), "python", StringComparison.OrdinalIgnoreCase)
            || string.Equals(language?.Trim(), "py", StringComparison.OrdinalIgnoreCase)
            ? "python"
            : "javascript";
    }

    public static string Extension(string? language)
    {
        return NormalizeLanguage(language) == "python" ? ".py" : ".js";
    }

    private static bool IsSafeScriptId(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value is not "." and not ".."
            && !value.Contains('\0')
            && !value.Contains(Path.DirectorySeparatorChar)
            && !value.Contains(Path.AltDirectorySeparatorChar)
            && !value.Contains(':');
    }
}
