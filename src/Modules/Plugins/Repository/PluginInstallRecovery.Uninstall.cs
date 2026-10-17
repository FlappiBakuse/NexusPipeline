using System.Text.Json;
using System.Security.Cryptography;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Modules.Plugins.Managed;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.Modules.Plugins.Repository;

internal static partial class PluginInstallRecovery
{
    private static void ApplyUninstall(
        PluginPendingOperation operation,
        PluginPendingState state,
        PluginOwnershipState ownership,
        string localPath,
        string backupPath,
        string pendingPath,
        string ownershipPath,
        string backupRoot)
    {
        EnsureChildPath(backupRoot, backupPath);
        bool ownershipSaved = false;
        try
        {
            if (operation.Phase == "pending")
            {
                if (string.IsNullOrWhiteSpace(operation.BackupPath))
                {
                    // 先写入稳定 backup 路径，再执行目录移动，进程中断时可精确恢复。
                    operation.BackupPath = backupPath;
                    SavePending(pendingPath, state);
                }
                if (PathExists(localPath) && !PathExists(backupPath))
                {
                    Directory.Move(localPath, backupPath);
                }
                if (PathExists(localPath) && PathExists(backupPath))
                {
                    throw new IOException($"卸载事务同时存在目标目录和 backup：{operation.Name}");
                }
                operation.Phase = "backed-up";
                SavePending(pendingPath, state);
            }
            if (operation.Phase == "backed-up")
            {
                // 卸载的目录移动完成后先把 swapped 阶段落盘，再处理 ownership；这样 ownership
                // 已删除而 pending 尚未清理时，下一次启动仍能识别这是本事务的可收尾现场。
                operation.Phase = "swapped";
                SavePending(pendingPath, state);
            }
            if (operation.Phase != "swapped" || PathExists(localPath))
            {
                throw new IOException($"卸载事务阶段无效：{operation.Name}/{operation.Phase}");
            }
            ownership.Plugins.RemoveAll(item => string.Equals(item.Name, operation.Name, StringComparison.OrdinalIgnoreCase));
            SaveOwnership(ownershipPath, ownership);
            ownershipSaved = true;
            DeletePath(backupPath);
            state.Operations.Remove(operation);
            SavePending(pendingPath, state);
        }
        catch
        {
            if (!ownershipSaved && operation.Phase == "backed-up" && !PathExists(localPath) && PathExists(backupPath))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                    Directory.Move(backupPath, localPath);
                    operation.Phase = "pending";
                    SavePending(pendingPath, state);
                }
                catch (Exception rollbackEx)
                {
                    Logger.Error($"[插件] 卸载失败事务回滚旧插件失败：{rollbackEx.Message}");
                }
            }
            throw;
        }
    }
}
