using NexusPipeline.Host.Composition;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Plugins.Repository;
using NexusPipeline.Modules.Settings.Persistence;
using NexusPipeline.Modules.Users.UseCases;
using NexusPipeline.Platform.Windows;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Modules.Users.Persistence;
using NexusPipeline.Modules.Plugins.DataSpecialized;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Host.Initialization;

/// <summary>
/// 常驻宿主初始化。调用方必须已经取得单实例互斥体；这里才允许接触并修复运行时实体持久化。
/// </summary>
internal static class HostedRuntimeInitializer
{
    public static bool Initialize(HostRuntime runtime)
    {
        try
        {
            PluginInstallRecovery.SeedBundledForNewInstall(RuntimeInitializer.BundledPluginSeedEligible);
            runtime.ReloadSettings(ConfigLoadMode.Repair);
            runtime.ReloadData();
            RuntimeDataReconciler.Reconcile(runtime);

            // 崩溃恢复仅常驻服务执行（manage/web/CLI 由运行时自愈 RecoverIfNeeded 兜底）。
            ConfigSwapSession.ConfigureRecovery(
                runtime.EntityState.FindScript,
                runtime.EntityState.SnapshotUsers,
                mark => mark.PendingConfigInput is not null
                    && runtime.UserCommands.CommitPendingConfigInput(
                        mark.UserId,
                        mark.ScriptId,
                        mark.PendingConfigInput.Name,
                        mark.PendingConfigInput.Value,
                        mark.PendingConfigInput.ExpectedBindingHash,
                        mark.PendingConfigInput.PreviousValue).Succeeded);
            ConfigWorkDirMaintenance.SweepRuntimeStaging();
            ConfigRecoveryService.RecoverInterrupted(runtime.EntityState.SnapshotUsers());
            runtime.History.RecoverInterruptedTasks(NexusPipeline.Modules.Execution.Judgement.TaskRunReducer.InterruptDailyReport);
            WindowsScheduledTaskRegistration.Sync(runtime.Settings.AutoStart);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Fatal($"[启动] 常驻运行时初始化失败，拒绝启动服务：{ex.Message}");
            Console.Error.WriteLine($"[FATAL] 常驻运行时初始化失败，拒绝启动服务：{ex.Message}");
            return false;
        }
    }
}
