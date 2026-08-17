using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>数据目录管理层（v0.5.0 从 UserConfigManager 拆出）：data/{脚本Id}/{用户名} 各类子目录的定位与清理。</summary>
internal static class ConfigSwapPaths
{
    public static string UserDir(string scriptId, string userName)
    {
        return Path.Combine(AppPaths.DataDir, scriptId, userName);
    }

    public static string StoreDir(string scriptId, string userName)
    {
        return Path.Combine(UserDir(scriptId, userName), "store");
    }

    public static string CacheDir(string scriptId, string userName)
    {
        return Path.Combine(UserDir(scriptId, userName), "original");
    }

    /// <summary>当前运行重试轮使用的临时配置快照；不等同于用户永久 store。</summary>
    public static string RetryStoreDir(string scriptId, string userName)
    {
        return Path.Combine(UserDir(scriptId, userName), "retry-store");
    }

    /// <summary>编辑会话隐藏配置暂存目录（编辑期间 config 同目录其他配置暂移至此，会话结束/重启恢复时移回）。</summary>
    public static string HiddenConfigDir(string scriptId, string userName)
    {
        return Path.Combine(UserDir(scriptId, userName), "edit-hidden");
    }

    /// <summary>判断脚本专用目录（可读写）；无用户时兜底 data/{脚本Id}/script。</summary>
    public static string ScriptDir(string scriptId, string? userName)
    {
        return string.IsNullOrWhiteSpace(userName)
            ? Path.Combine(AppPaths.DataDir, scriptId, "script")
            : Path.Combine(UserDir(scriptId, userName), "script");
    }

    /// <summary>配置替换备份目录（无用户交换时用于还原；有用户时由配置交换机制还原，备份作双保险）。</summary>
    public static string ReplaceBackupDir(string scriptId, string? userName)
    {
        return string.IsNullOrWhiteSpace(userName)
            ? Path.Combine(AppPaths.DataDir, scriptId, "swap-backup")
            : Path.Combine(UserDir(scriptId, userName), "swap-backup");
    }

    /// <summary>准备判断脚本目录：清空重建（运行开始调用）。</summary>
    public static void PrepareScriptDir(string scriptId, string? userName)
    {
        string dir = ScriptDir(scriptId, userName);
        ConfigSwapPrimitives.TryDeleteDir(dir);
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 准备判断脚本目录失败（{dir}）：{ex.Message}");
        }
    }

    /// <summary>运行结束清理：清空判断脚本目录与配置替换备份目录。</summary>
    public static void CleanupScriptArea(string scriptId, string? userName)
    {
        ConfigSwapPrimitives.TryDeleteDir(ScriptDir(scriptId, userName));
        if (!string.IsNullOrWhiteSpace(userName))
        {
            ConfigSwapPrimitives.TryDeleteDir(RetryStoreDir(scriptId, userName));
        }
    }

    /// <summary>数据目录命名迁移（v0.6.0）：旧名 → 新名（config→store、cache→original、edit-hide→edit-hidden、replace-backup→swap-backup）。
    /// 幂等：目标名已存在则跳过（保留新现场）；失败仅告警不阻断。启动时在崩溃恢复扫描前调用，确保旧版本残留现场仍可恢复。</summary>
    public static void MigrateLegacyLayout()
    {
        if (!Directory.Exists(AppPaths.DataDir))
        {
            return;
        }
        foreach (string scriptDir in Directory.GetDirectories(AppPaths.DataDir))
        {
            string scriptId = Path.GetFileName(scriptDir);
            foreach ((string oldName, string newName) in LegacyDirMap)
            {
                TryRenameDir(Path.Combine(scriptDir, oldName), Path.Combine(scriptDir, newName), $"{scriptId}（无用户）");
                foreach (string userDir in Directory.GetDirectories(scriptDir))
                {
                    string userName = Path.GetFileName(userDir);
                    TryRenameDir(Path.Combine(userDir, oldName), Path.Combine(userDir, newName), $"{scriptId} / {userName}");
                }
            }
        }
    }

    private static readonly (string Old, string New)[] LegacyDirMap =
    {
        ("config", "store"),
        ("cache", "original"),
        ("edit-hide", "edit-hidden"),
        ("replace-backup", "swap-backup"),
    };

    private static void TryRenameDir(string oldPath, string newPath, string scope)
    {
        if (!Directory.Exists(oldPath))
        {
            return;
        }
        if (Directory.Exists(newPath))
        {
            Logger.Warn($"[警告] 数据目录命名迁移跳过（目标已存在）：{oldPath} → {newPath}");
            return;
        }
        try
        {
            Directory.Move(oldPath, newPath);
            Logger.Info($"[迁移] 数据目录命名更新：{oldPath} → {newPath}（{scope}）");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 数据目录命名迁移失败（{scope}）：{oldPath} → {newPath}（{ex.Message}）");
        }
    }

    /// <summary>删除脚本时清理其全部数据目录。</summary>
    public static void RemoveScriptData(string scriptId)
    {
        string dir = Path.Combine(AppPaths.DataDir, scriptId);
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 清理脚本数据目录失败（{dir}）：{ex.Message}");
        }
    }

    /// <summary>删除用户时清理其数据目录。</summary>
    public static void RemoveUserData(string scriptId, string userName)
    {
        string dir = UserDir(scriptId, userName);
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 清理用户数据目录失败（{dir}）：{ex.Message}");
        }
    }

    /// <summary>用户改名时迁移其数据目录；失败返回错误信息（由调用方决定不落盘，名称与数据保持原样）。</summary>
    public static string? RenameUserData(string scriptId, string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string oldDir = UserDir(scriptId, oldName);
        string newDir = UserDir(scriptId, newName);
        try
        {
            if (!Directory.Exists(oldDir))
            {
                return null;
            }
            if (Directory.Exists(newDir))
            {
                Directory.Delete(newDir, recursive: true);
            }
            try
            {
                Directory.Move(oldDir, newDir);
            }
            catch (IOException)
            {
                ConfigSwapPrimitives.MoveAs(oldDir, newDir, PathKind.Dir);
            }
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 用户数据目录迁移失败（{oldDir} → {newDir}）：{ex.Message}");
            return $"用户数据目录迁移失败：{ex.Message}";
        }
    }
}
