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
    private static void ApplyOne(
        PluginPendingOperation operation,
        PluginPendingState state,
        PluginOwnershipState ownership,
        string pluginsDir,
        string pendingPath,
        string ownershipPath,
        string stagingRoot,
        string backupRoot,
        IPluginActivationPreferences? activationPreferences)
    {
        if (!PluginRepositoryCatalog.IsCanonicalPluginId(operation.Name))
        {
            throw new InvalidDataException($"pending 包含不安全插件名称：{operation.Name}");
        }
        if (!PluginRepositoryCatalog.IsSafeArtifactName(operation.ArtifactName))
        {
            throw new InvalidDataException($"pending 包含不安全物理目录名：{operation.ArtifactName}");
        }
        string localPath = Path.Combine(pluginsDir, operation.ArtifactName);
        EnsureChildPath(pluginsDir, localPath);
        string stagedPath = Path.GetFullPath(operation.StagedPath ?? "");
        EnsureChildPath(stagingRoot, stagedPath);
        string backupPath = string.IsNullOrWhiteSpace(operation.BackupPath)
            ? Path.Combine(backupRoot, operation.ArtifactName + "." + Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(operation.BackupPath);
        EnsureChildPath(backupRoot, backupPath);

        if (operation.Action is not ("install" or "update" or "uninstall"))
        {
            throw new InvalidDataException($"pending 操作无效：{operation.Action}");
        }
        if (operation.Action == "uninstall")
        {
            if (operation.Phase is "pending" or "backed-up")
            {
                EnsureOwnedIdentity(operation, ownership, requireCurrentVersion: true);
            }
            ApplyUninstall(operation, state, ownership, localPath, backupPath, pendingPath, ownershipPath, backupRoot);
            return;
        }

        PluginOwnership? currentOwner = FindOwnership(operation.Name, ownership);
        if (operation.Action == "install"
            && operation.Phase == "pending"
            && currentOwner is not null
            && !string.Equals(currentOwner.ArtifactName, operation.ArtifactName, StringComparison.Ordinal))
        {
            throw new IOException($"安装事务归属 artifactName 不匹配，拒绝替换目录：{operation.Name}");
        }
        if (operation.Action == "install"
            && operation.Phase == "pending"
            && PathExists(localPath))
        {
            throw new IOException($"安装事务目标目录已存在，拒绝覆盖：{operation.Name}");
        }
        if (operation.Action == "update" && (operation.Phase is "pending" or "backed-up"))
        {
            EnsureOwnedIdentity(operation, ownership, requireCurrentVersion: false);
        }

        try
        {
            if (operation.Phase is "pending" or "backed-up")
            {
                if (operation.Phase == "pending" && string.IsNullOrWhiteSpace(operation.BackupPath))
                {
                    // 先写入稳定 backup 路径，再执行目录移动，进程中断时可精确恢复。
                    operation.BackupPath = backupPath;
                    SavePending(pendingPath, state);
                }
                if (PathExists(localPath) && !PathExists(backupPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    Directory.Move(localPath, backupPath);
                }
                if (PathExists(localPath) && PathExists(backupPath))
                {
                    if (!PathExists(stagedPath))
                    {
                        // 目录交换已完成但 phase 尚未落盘，按完成阶段继续收尾。
                        operation.Phase = "swapped";
                        SavePending(pendingPath, state);
                    }
                    else
                    {
                        throw new IOException($"插件事务同时存在旧目录、新目录和 staging：{operation.Name}");
                    }
                }
                else
                {
                    operation.Phase = "backed-up";
                    SavePending(pendingPath, state);
                }
            }

            if (operation.Phase is "pending" or "backed-up")
            {
                if (!Directory.Exists(stagedPath))
                {
                    throw new IOException($"插件 staging 不存在：{stagedPath}");
                }
                if (PathExists(localPath))
                {
                    // 进程可能在目录交换后、journal 写入前退出；backup + 目标目录同时存在时按已交换恢复幂等阶段。
                    if (operation.Phase == "backed-up" && PathExists(backupPath))
                    {
                        operation.Phase = "swapped";
                        SavePending(pendingPath, state);
                    }
                    else
                    {
                        throw new IOException($"插件目标目录仍存在：{localPath}");
                    }
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                    Directory.Move(stagedPath, localPath);
                    operation.Phase = "swapped";
                    SavePending(pendingPath, state);
                }
            }
        }
        catch
        {
            // 交换前失败时恢复旧插件，保留 pending 让下次启动重新尝试；交换完成后的阶段保持幂等现场。
            if (operation.Phase == "backed-up" && !PathExists(localPath) && PathExists(backupPath))
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
                    Logger.Error($"[插件] 失败事务回滚旧插件失败：{rollbackEx.Message}");
                }
            }
            throw;
        }

        if (operation.Phase != "swapped" || !Directory.Exists(localPath))
        {
            throw new IOException($"插件事务阶段无效：{operation.Name}/{operation.Phase}");
        }
        UpsertOwnership(ownership, new PluginOwnership
        {
            Name = operation.Name,
            ArtifactName = operation.ArtifactName,
            Version = operation.Version,
            Kind = operation.Kind,
            ApiVersion = operation.ApiVersion,
            Sha256 = operation.Sha256,
            Channel = operation.Channel,
            SourceCommit = operation.SourceCommit,
            InstalledAt = DateTimeOffset.UtcNow,
        });
        SaveOwnership(ownershipPath, ownership);
        if (operation.EnableAfterInstall)
        {
            PluginOwnership owner = FindOwnership(operation.Name, ownership)
                ?? throw new IOException($"插件安装归属未提交：{operation.Name}");
            if (operation.Action != "install" || operation.Kind != "managed-code"
                || owner.ArtifactName != operation.ArtifactName
                || owner.Version != operation.Version
                || !string.Equals(owner.Sha256, operation.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"插件安装归属与启用意图不一致：{operation.Name}");
            }
            PluginActivationResult result = activationPreferences?.ApplyInstallIntent(
                new PluginInstallCompletion(operation.OperationId, operation.Name, operation.ArtifactName,
                    operation.Kind, operation.Action, operation.EnableAfterInstall, owner))
                ?? PluginActivationResult.Failed;
            if (result is not (PluginActivationResult.Enabled or PluginActivationResult.Preserved))
            {
                throw new IOException($"插件启用偏好保存失败，保留安装事务：{operation.Name}");
            }
        }
        DeletePath(backupPath);
        state.Operations.Remove(operation);
        SavePending(pendingPath, state);
    }
}
