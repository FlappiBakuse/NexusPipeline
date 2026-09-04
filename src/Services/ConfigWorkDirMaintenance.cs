using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>
/// work/ 事务工作区的当前格式维护：启动恢复完成后清扫已无恢复价值的空闲工作目录。
/// 仍在使用的会话标记、配置还原现场和增量快照事务由恢复流程保留并继续处理。
/// </summary>
internal static class ConfigWorkDirMaintenance
{
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

    private static void SweepOne(string ownerDir, bool hasSessionMark)
    {
        string workDir = Path.Combine(ownerDir, ConfigSwapPaths.WorkDirName);
        if (!Directory.Exists(workDir) || hasSessionMark)
        {
            return;
        }
        // 判断脚本目录每轮清空重建、无恢复价值，可直接删除。
        ConfigSwapPrimitives.TryDeleteDir(Path.Combine(workDir, "script"));
        // 其余子项可能承载恢复现场（swap-backup/original/edit-hidden/store-txn）：
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
