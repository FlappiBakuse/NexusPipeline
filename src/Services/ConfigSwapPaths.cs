using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>数据目录管理层（v0.5.0 从 UserConfigManager 拆出）：data/{脚本Id}/{UserId} 各类子目录的定位与清理。</summary>
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

    /// <summary>
    /// 启动恢复使用的受限目录迁移：脚本根目录保留无用户现场，用户目录只处理当前全局用户绑定对应的 UserId。
    /// 用户名遗留目录不会进入此路径。
    /// </summary>
    internal static void MigrateLegacyLayoutForRecovery(
        string dataDir,
        IReadOnlyDictionary<string, HashSet<string>> userKeysByScript)
    {
        if (!Directory.Exists(dataDir))
        {
            return;
        }
        foreach (string scriptDir in Directory.GetDirectories(dataDir))
        {
            string scriptId = Path.GetFileName(scriptDir);
            foreach ((string oldName, string newName) in LegacyDirMap)
            {
                string oldPath = Path.Combine(scriptDir, oldName);
                if (LooksLikeUserDataDir(oldPath))
                {
                    Logger.Warn($"[迁移] 跳过疑似保留名用户目录，避免误改用户数据：{oldPath}");
                }
                else
                {
                    TryRenameDir(oldPath, Path.Combine(scriptDir, newName), $"{scriptId}（无用户）");
                }
            }

            if (!userKeysByScript.TryGetValue(scriptId, out HashSet<string>? userKeys))
            {
                continue;
            }
            foreach (string userKey in userKeys)
            {
                string userDir = Path.Combine(scriptDir, userKey);
                if (!Directory.Exists(userDir))
                {
                    continue;
                }
                foreach ((string oldName, string newName) in LegacyDirMap)
                {
                    TryRenameDir(
                        Path.Combine(userDir, oldName),
                        Path.Combine(userDir, newName),
                        $"{scriptId} / {userKey}");
                }
            }
        }
    }

    /// <summary>
    /// 迁移指定数据根目录的脚本级旧目录命名；保留给旧布局隔离测试。
    /// 生产启动恢复使用上面的 UserId 白名单重载，避免枚举脚本根下的任意用户目录。
    /// </summary>
    internal static void MigrateLegacyLayout(string dataDir)
    {
        if (!Directory.Exists(dataDir))
        {
            return;
        }
        foreach (string scriptDir in Directory.GetDirectories(dataDir))
        {
            string scriptId = Path.GetFileName(scriptDir);
            foreach ((string oldName, string newName) in LegacyDirMap)
            {
                string oldPath = Path.Combine(scriptDir, oldName);
                if (LooksLikeUserDataDir(oldPath))
                {
                    Logger.Warn($"[迁移] 跳过疑似保留名用户目录，避免误改用户数据：{oldPath}");
                }
                else
                {
                    TryRenameDir(oldPath, Path.Combine(scriptDir, newName), $"{scriptId}（无用户）");
                }
            }
        }
    }

    private static bool LooksLikeUserDataDir(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }
        string[] markers = { "store", "original", "retry-store", "script", "swap-backup", "replace-backup", "edit-hidden", "edit-hide", ".session" };
        return markers.Any(marker => Directory.Exists(Path.Combine(path, marker)) || File.Exists(Path.Combine(path, marker)));
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
