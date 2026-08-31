using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>数据目录管理层（从 UserConfigManager 拆出）：data/{脚本Id}/{UserId} 各类子目录的定位与清理。</summary>
internal static class ConfigSwapPaths
{
    public static string UserDir(string scriptId, string userKey)
    {
        return Path.Combine(AppPaths.DataDir, scriptId, userKey);
    }

    public static string StoreDir(string scriptId, string userKey)
    {
        return Path.Combine(UserDir(scriptId, userKey), "store");
    }

    public static string CacheDir(string scriptId, string userKey)
    {
        return Path.Combine(UserDir(scriptId, userKey), "original");
    }

    /// <summary>当前运行重试轮使用的临时配置快照；不等同于用户永久 store。</summary>
    public static string RetryStoreDir(string scriptId, string userKey)
    {
        return Path.Combine(UserDir(scriptId, userKey), "retry-store");
    }

    /// <summary>编辑会话隐藏配置暂存目录（编辑期间 config 同目录其他配置暂移至此，会话结束/重启恢复时移回）。</summary>
    public static string HiddenConfigDir(string scriptId, string userKey)
    {
        return Path.Combine(UserDir(scriptId, userKey), "edit-hidden");
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

    /// <summary>删除脚本时将其全部数据目录移入应用根目录下的隔离区，保留人工恢复机会。</summary>
    public static bool RemoveScriptData(string scriptId)
    {
        string dir = Path.Combine(AppPaths.DataDir, scriptId);
        try
        {
            if (!Directory.Exists(dir))
            {
                return true;
            }
            string trashRoot = Path.Combine(AppPaths.AppRoot, "data-trash");
            Directory.CreateDirectory(trashRoot);
            string target = Path.Combine(trashRoot, $"{scriptId}-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
            Directory.Move(dir, target);
            Logger.Info($"脚本数据目录已移入隔离区：{target}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 隔离脚本数据目录失败（{dir}，原数据保留）：{ex.Message}");
            return false;
        }
    }

    /// <summary>删除用户绑定时清理其 UserId 数据目录。</summary>
    public static void RemoveUserData(string scriptId, string userKey)
    {
        string dir = UserDir(scriptId, userKey);
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

}
