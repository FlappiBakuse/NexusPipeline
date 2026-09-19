using NexusPipeline.Host.Composition;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Settings.Persistence;
using NexusPipeline.Modules.Users.UseCases;
using NexusPipeline.Platform.Windows;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Modules.Configuration.Recovery;

namespace NexusPipeline.Host.Initialization;

/// <summary>
/// 常驻宿主初始化。调用方必须已经取得单实例互斥体；这里才允许接触并修复运行时实体持久化。
/// </summary>
internal static class HostedRuntimeInitializer
{
    public static bool Initialize(HostCompositionRoot ctx)
    {
        try
        {
            ctx.ReloadSettings(ConfigLoadMode.Repair);
            ctx.ReloadData();
            PluginNameMigration.Apply(
                ctx.Settings,
                ctx.EntityState.SnapshotScripts(),
                ctx.ReplaceSettings,
                scripts => ctx.EntityState.Mutate(state =>
                {
                    state.Scripts.Clear();
                    state.Scripts.AddRange(scripts.Select(script => script.Clone()));
                }));
            RuntimeDataReconciler.Reconcile(ctx);

            // 崩溃恢复仅常驻服务执行（manage/web/CLI 由运行时自愈 RecoverIfNeeded 兜底）。
            ConfigSwapSession.ConfigureRecovery(
                ctx.EntityState.FindScript,
                ctx.EntityState.SnapshotUsers,
                ctx.Resolve<UserCommands>().TryCommitPendingConfigInput);
            ConfigWorkDirMaintenance.SweepRuntimeStaging();
            ConfigRecoveryService.RecoverInterrupted(ctx.EntityState.SnapshotUsers());
            WindowsScheduledTaskRegistration.Sync(ctx.Settings.AutoStart);
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
