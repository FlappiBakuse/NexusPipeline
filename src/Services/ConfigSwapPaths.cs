using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>
/// 数据目录管理层（从 UserConfigManager 拆出）：data/{脚本Id}/{UserId} 各类子目录的定位与清理。
/// v0.13.2 空闲态只保留一份权威快照 store 与小型 store-meta.json；
/// work/ 下保留运行现场与增量 store-txn，提交完成后事务目录清理。
/// store-archive、store-previous、store-tmp、retry-store 仅作为旧版本迁移入口识别，禁止新流程创建。
/// </summary>
internal static class ConfigSwapPaths
{
    /// <summary>会话事务工作区目录名（v0.13.2 起归并宿主；v0.13.0 及更早为散落的顶层子目录）。</summary>
    public const string WorkDirName = "work";

    /// <summary>v0.13.0 及更早版本散落在用户/脚本目录顶层的事务目录名 → work/ 内规范名（dot 后缀一并改为 kebab-case）。</summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyWorkItemMap = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["original"] = "original",
        ["script"] = "script",
        ["swap-backup"] = "swap-backup",
        ["edit-hidden"] = "edit-hidden",
        ["retry-store"] = "retry-store",
        ["store.tmp"] = "store-tmp",
    };

    /// <summary>v0.13.0 及更早版本用户目录顶层的 dot 后缀命名 → kebab-case 规范名（原地改名）。</summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyDotSuffixRenames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["store.previous"] = "store-previous",
        ["store.meta.json"] = "store-meta.json",
    };

    public static string UserDir(string scriptId, string userKey)
    {
        return Path.Combine(AppPaths.DataDir, scriptId, userKey);
    }

    /// <summary>会话事务工作区：仅会话期间存在；无用户时兜底 data/{脚本Id}/work。</summary>
    public static string WorkDir(string scriptId, string? userName)
    {
        return string.IsNullOrWhiteSpace(userName)
            ? Path.Combine(AppPaths.DataDir, scriptId, WorkDirName)
            : Path.Combine(UserDir(scriptId, userName), WorkDirName);
    }

    public static string StoreDir(string scriptId, string userKey)
    {
        return Path.Combine(UserDir(scriptId, userKey), "store");
    }

    /// <summary>用户快照元数据；记录 profile/配置定位指纹，不保存插件 profile 内容。</summary>
    public static string StoreMetadataPath(string scriptId, string userKey)
    {
        return Path.Combine(UserDir(scriptId, userKey), "store-meta.json");
    }

    /// <summary>旧版持久快照归档目录；v0.13.2 不再创建，仅用于安全迁移。</summary>
    public static string StoreArchiveDir(string scriptId, string userKey)
    {
        return Path.Combine(UserDir(scriptId, userKey), "store-archive");
    }

    /// <summary>旧版完整上一代快照；v0.13.2 不再创建，仅用于启动恢复与安全迁移。</summary>
    public static string StorePreviousDir(string scriptId, string userKey)
    {
        return Path.Combine(UserDir(scriptId, userKey), "store-previous");
    }

    /// <summary>旧版全量镜像暂存目录；v0.13.2 不再创建，仅用于启动恢复与安全迁移。</summary>
    public static string StoreTempDir(string scriptId, string userKey)
    {
        return Path.Combine(WorkDir(scriptId, userKey), "store-tmp");
    }

    public static string CacheDir(string scriptId, string userKey)
    {
        return Path.Combine(WorkDir(scriptId, userKey), "original");
    }

    /// <summary>旧版重试完整快照；v0.13.2 重试直接复用当前活动配置，不再创建。</summary>
    public static string RetryStoreDir(string scriptId, string userKey)
    {
        return Path.Combine(WorkDir(scriptId, userKey), "retry-store");
    }

    /// <summary>编辑会话隐藏配置暂存目录（编辑期间 config 同目录其他配置暂移至此，会话结束/重启恢复时移回）。</summary>
    public static string HiddenConfigDir(string scriptId, string userKey)
    {
        return Path.Combine(WorkDir(scriptId, userKey), "edit-hidden");
    }

    /// <summary>判断脚本专用目录（可读写）；无用户时兜底 data/{脚本Id}/work/script。</summary>
    public static string ScriptDir(string scriptId, string? userName)
    {
        return Path.Combine(WorkDir(scriptId, userName), "script");
    }

    /// <summary>配置替换备份目录（无用户交换时用于还原；有用户时由配置交换机制还原，备份作双保险）。</summary>
    public static string ReplaceBackupDir(string scriptId, string? userName)
    {
        return Path.Combine(WorkDir(scriptId, userName), "swap-backup");
    }

    /// <summary>配置快照增量事务目录：manifest/stage/rollback/commit 只保存本轮变更。</summary>
    public static string StoreTransactionDir(string scriptId, string userKey)
    {
        return Path.Combine(WorkDir(scriptId, userKey), "store-txn");
    }

    public static string StoreTransactionManifestPath(string scriptId, string userKey) =>
        Path.Combine(StoreTransactionDir(scriptId, userKey), "manifest.json");

    public static string StoreTransactionStageDir(string scriptId, string userKey) =>
        Path.Combine(StoreTransactionDir(scriptId, userKey), "stage");

    public static string StoreTransactionRollbackDir(string scriptId, string userKey) =>
        Path.Combine(StoreTransactionDir(scriptId, userKey), "rollback");

    public static string StoreTransactionCommitPath(string scriptId, string userKey) =>
        Path.Combine(StoreTransactionDir(scriptId, userKey), "commit.json");

    /// <summary>损坏事务的人工处理阻断标记；存在时禁止继续写入该用户快照。</summary>
    public static string StoreTransactionBlockedPath(string scriptId, string userKey) =>
        Path.Combine(UserDir(scriptId, userKey), ".store-txn-blocked.json");

    /// <summary>配置定位变更时的一次性旧快照隔离区；成功重建新快照后删除。</summary>
    public static string StoreRebindDir(string scriptId, string userKey) =>
        Path.Combine(WorkDir(scriptId, userKey), "store-rebind");

    public static string StoreRebindOldDir(string scriptId, string userKey) =>
        Path.Combine(StoreRebindDir(scriptId, userKey), "old");

    public static string StoreRebindNewMetadataPath(string scriptId, string userKey) =>
        Path.Combine(StoreRebindDir(scriptId, userKey), "new-store-meta.json");

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
