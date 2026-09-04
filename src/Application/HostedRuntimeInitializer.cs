using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline;

/// <summary>
/// 常驻宿主初始化。调用方必须已经取得单实例互斥体；这里才允许接触并修复运行时实体持久化。
/// </summary>
internal static class HostedRuntimeInitializer
{
    public static bool Initialize(RuntimeContext ctx)
    {
        try
        {
            ctx.ReloadSettings(ConfigLoadMode.Repair);
            ctx.ReloadData();
            RuntimeDataReconciler.Reconcile(ctx);

            // 崩溃恢复仅常驻服务执行（manage/web/CLI 由运行时自愈 RecoverIfNeeded 兜底）。
            ConfigSwapSession.ConfigureRecovery(ctx.EntityState.FindScript, ctx.EntityState.SnapshotUsers);
            ConfigWorkDirMaintenance.SweepRuntimeStaging();
            UserConfigManager.RecoverInterrupted(ctx.EntityState.SnapshotUsers());
            TaskRegistration.SyncWithSettings(ctx.Settings);
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
