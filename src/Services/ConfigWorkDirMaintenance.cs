using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>
/// work/ 事务工作区的布局维护（v0.13.2）：
/// - 一次性迁移：把 v0.13.0 及更早散落在 data/{脚本Id}/[用户目录]/ 顶层的事务目录按
///   <see cref="ConfigSwapPaths.LegacyWorkItemMap"/> 移入 work/（dot 后缀同步改为 kebab-case 规范名），
///   并把用户目录顶层旧 dot 后缀命名的持久层条目（store.previous、store.meta.json）按
///   <see cref="ConfigSwapPaths.LegacyDotSuffixRenames"/> 原地改名；在启动恢复扫描之前执行，
///   保证旧版本崩溃现场仍按原语义恢复；幂等（旧路径缺失或新路径已存在时跳过，冲突保留现场）。
/// - 空闲清扫：启动恢复完成后，删除 work/ 中无恢复价值的残留（script、旧 retry-store 与空目录）；
///   存在 .session 标记或不可丢弃残留（swap-backup、original、edit-hidden、store-tmp、store-txn 有内容）时保留现场。
/// </summary>
internal static class ConfigWorkDirMaintenance
{
    /// <summary>把旧版散落的顶层事务目录与旧命名持久层条目迁移为 v0.13.2 规范布局；在 RuntimeInitializer 中对所有进程模式执行。</summary>
    public static void MigrateLegacyWorkDirs()
    {
        if (!Directory.Exists(AppPaths.DataDir))
        {
            return;
        }
        foreach (string scriptDir in Directory.GetDirectories(AppPaths.DataDir))
        {
            MigrateOne(scriptDir);
            foreach (string userDir in Directory.GetDirectories(scriptDir))
            {
                if (string.Equals(Path.GetFileName(userDir), ConfigSwapPaths.WorkDirName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                MigrateOne(userDir);
            }
        }
    }

    /// <summary>启动恢复完成后清扫空闲 work/；仅 service/web 的 RecoverInterrupted 末尾调用。</summary>
    public static void SweepIdleWorkDirs()
    {
        if (!Directory.Exists(AppPaths.DataDir))
        {
            return;
        }
        foreach (string scriptDir in Directory.GetDirectories(AppPaths.DataDir))
        {
            SweepOne(scriptDir, hasSessionMark: false);
            foreach (string userDir in Directory.GetDirectories(scriptDir))
            {
                if (string.Equals(Path.GetFileName(userDir), ConfigSwapPaths.WorkDirName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                bool hasSessionMark = File.Exists(Path.Combine(userDir, ".session"))
                    || File.Exists(Path.Combine(userDir, ".session.bak"));
                SweepOne(userDir, hasSessionMark);
            }
        }
    }

    /// <summary>清空 .nxp/runtime/staging（可重建暂存区）：删除上次进程残留的上传/解压临时文件。</summary>
    public static void SweepRuntimeStaging()
    {
        try
        {
            if (Directory.Exists(AppPaths.RuntimeStagingDir))
            {
                Directory.Delete(AppPaths.RuntimeStagingDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 清理运行时暂存目录失败（{AppPaths.RuntimeStagingDir}）：{ex.Message}");
        }
    }

    private static void MigrateOne(string ownerDir)
    {
        // 持久层旧 dot 后缀命名原地改名（仅用户目录存在，脚本级检查无副作用）。
        foreach (KeyValuePair<string, string> rename in ConfigSwapPaths.LegacyDotSuffixRenames)
        {
            RenameIfPresent(ownerDir, rename.Key, rename.Value);
        }
        string workDir = Path.Combine(ownerDir, ConfigSwapPaths.WorkDirName);
        foreach (KeyValuePair<string, string> legacy in ConfigSwapPaths.LegacyWorkItemMap)
        {
            string source = Path.Combine(ownerDir, legacy.Key);
            if (!Directory.Exists(source) && !File.Exists(source))
            {
                continue;
            }
            string destination = Path.Combine(workDir, legacy.Value);
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                // 同名目标已存在说明现场不是预期的旧布局，保留原样交由恢复逻辑与人工核查。
                Logger.Warn($"[迁移] work/ 内已存在同名目录，跳过迁移（保留现场）：{source}");
                continue;
            }
            try
            {
                Directory.CreateDirectory(workDir);
                if (Directory.Exists(source))
                {
                    Directory.Move(source, destination);
                }
                else
                {
                    File.Move(source, destination);
                }
                Logger.Info($"[迁移] 会话事务目录已归并：{source} → {destination}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 迁移会话事务目录失败（保留原样）：{source}：{ex.Message}");
            }
        }
        // 兼容开发期已按中间布局迁移过的现场：work/ 内旧 store.tmp 改名为 store-tmp。
        RenameIfPresent(workDir, "store.tmp", "store-tmp");
    }

    /// <summary>同目录内改名：旧名存在且新名不存在才执行，冲突保留现场并告警。</summary>
    private static void RenameIfPresent(string ownerDir, string oldName, string newName)
    {
        string source = Path.Combine(ownerDir, oldName);
        if (!Directory.Exists(source) && !File.Exists(source))
        {
            return;
        }
        string destination = Path.Combine(ownerDir, newName);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            Logger.Warn($"[迁移] 规范名已存在，跳过旧名改名（保留现场）：{source}");
            return;
        }
        try
        {
            if (Directory.Exists(source))
            {
                Directory.Move(source, destination);
            }
            else
            {
                File.Move(source, destination);
            }
            Logger.Info($"[迁移] 目录条目已按命名规范改名：{source} → {destination}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 改名失败（保留原样）：{source}：{ex.Message}");
        }
    }

    private static void SweepOne(string ownerDir, bool hasSessionMark)
    {
        string workDir = Path.Combine(ownerDir, ConfigSwapPaths.WorkDirName);
        if (!Directory.Exists(workDir) || hasSessionMark)
        {
            return;
        }
        // 脚本工作目录与旧版重试快照每轮清空重建、无恢复价值，可直接删除。
        ConfigSwapPrimitives.TryDeleteDir(Path.Combine(workDir, "script"));
        ConfigSwapPrimitives.TryDeleteDir(Path.Combine(workDir, "retry-store"));
        // 其余子项可能承载恢复现场（swap-backup/original/edit-hidden/store-tmp/store-txn）：
        // 只清掉空目录，存在任何内容时整体保留 work/，交由恢复逻辑或人工处理。
        foreach (string entry in Directory.GetFileSystemEntries(workDir))
        {
            if (!Directory.Exists(entry) || Directory.EnumerateFileSystemEntries(entry).Any())
            {
                continue;
            }
            try
            {
                Directory.Delete(entry);
            }
            catch (Exception ex)
            {
                Logger.Debug($"清理 work/ 空目录失败（保留）：{entry}：{ex.Message}");
            }
        }
        if (!Directory.EnumerateFileSystemEntries(workDir).Any())
        {
            try
            {
                Directory.Delete(workDir);
            }
            catch (Exception ex)
            {
                Logger.Debug($"清理空闲 work/ 失败（保留）：{workDir}：{ex.Message}");
            }
        }
    }
}
