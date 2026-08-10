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
        return Path.Combine(UserDir(scriptId, userName), "config");
    }

    public static string CacheDir(string scriptId, string userName)
    {
        return Path.Combine(UserDir(scriptId, userName), "cache");
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
            ? Path.Combine(AppPaths.DataDir, scriptId, "replace-backup")
            : Path.Combine(UserDir(scriptId, userName), "replace-backup");
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
        ConfigSwapPrimitives.TryDeleteDir(ReplaceBackupDir(scriptId, userName));
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
