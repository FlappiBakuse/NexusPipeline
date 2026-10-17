using System.Text.Json;
using System.Security.Cryptography;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Modules.Plugins.Managed;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.Modules.Plugins.Repository;

/// <summary>插件安装、更新、卸载的跨重启 journal 与启动时文件切换。</summary>
internal static partial class PluginInstallRecovery
{
    private static readonly object Sync = new();
    internal const int CurrentStateSchema = 3;

    /// <summary>在 PluginManager.LoadAll 前应用所有已验证 staging；任一步失败均保留 pending 供下一次启动重试。</summary>
    public static bool ApplyPending(
        string? pluginsDir = null,
        string? pendingPath = null,
        string? ownershipPath = null,
        string? stagingRoot = null,
        string? backupRoot = null,
        IPluginActivationPreferences? activationPreferences = null)
    {
        string localPlugins = Path.GetFullPath(pluginsDir ?? AppPaths.PluginsDir);
        string journalPath = Path.GetFullPath(pendingPath ?? AppPaths.PluginPendingPath);
        string ownersPath = Path.GetFullPath(ownershipPath ?? AppPaths.PluginOwnershipPath);
        string stagingBase = Path.GetFullPath(stagingRoot ?? AppPaths.PluginStagingDir);
        string backupBase = Path.GetFullPath(backupRoot ?? AppPaths.PluginBackupDir);
        lock (Sync)
        {
            PluginPendingState state;
            try
            {
                state = LoadPending(journalPath);
            }
            catch (Exception ex)
            {
                Logger.Error($"[插件] 读取安装事务失败，保留 journal：{ex.Message}");
                return false;
            }
            if (state.Operations.Count == 0)
            {
                return true;
            }

            PluginOwnershipState ownership;
            try
            {
                ownership = LoadOwnership(ownersPath);
            }
            catch (Exception ex)
            {
                Logger.Error($"[插件] 读取安装归属失败，保留 pending：{ex.Message}");
                return false;
            }
            Directory.CreateDirectory(localPlugins);
            Directory.CreateDirectory(stagingBase);
            Directory.CreateDirectory(backupBase);
            try
            {
                foreach (PluginPendingOperation operation in state.Operations.ToArray())
                {
                    ApplyOne(operation, state, ownership, localPlugins, journalPath, ownersPath, stagingBase, backupBase, activationPreferences);
                }
                TryDeleteEmptyDirectories(stagingBase);
                TryDeleteEmptyDirectories(backupBase);
                TryDeleteEmptyDirectory(Path.GetDirectoryName(journalPath)!);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[插件] 安装事务未完成，保留 pending 供下次启动恢复：{ex.Message}");
                return false;
            }
            finally
            {
                PluginInstalledInventory.Invalidate(localPlugins);
            }
        }
    }
}
