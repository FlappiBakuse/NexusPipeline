using NexusPipeline.App.Repositories;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Services.Update;
using NexusPipeline.Utilities;

namespace NexusPipeline;

/// <summary>
/// 应用运行时初始化：权限契约、约束加载以及共享数据加载。
/// 该阶段只负责建立可运行的组合根，不启动服务或处理具体命令。
/// </summary>
internal static class RuntimeInitializer
{
    public static int Initialize()
    {
        if (!IsTestHost() && !IsAdministrator())
        {
            const string msg = "NexusPipeline 必须以管理员身份运行（脚本程序需要管理员权限才能被接管运行），当前实例未获得管理员权限，即将退出。请右键「以管理员身份运行」，或确认部署的是提权版（requireAdministrator）。";
            Logger.Fatal(msg);
            Console.Error.WriteLine($"[FATAL] {msg}");
            try
            {
                System.Windows.Forms.MessageBox.Show(msg, "NexusPipeline 需要管理员权限", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch
            {
            }
            return 2;
        }

        UpdateApply.CleanupWorkerImages();
        // 先加载约束，再加载设置（Normalize 使用固定的历史保留天数上限）。
        Limits.Load();
        RuntimeContext ctx = RuntimeContext.Instance;
        ctx.ReloadSettings();
        ctx.ReloadData();
        // 配置交换恢复的数据源由组合根装配（恢复路径不再反向依赖 RuntimeContext；
        // 所有进程模式共用，service/web 的 StartupPipeline 启动恢复与 CLI 运行时自愈均由此覆盖）。
        ConfigSwapSession.ConfigureRecovery(ctx.FindScript, ctx.SnapshotUsers);
        // v0.13.1 布局迁移：旧散落事务目录归并进 work/，必须在任何恢复扫描/自愈之前完成。
        ConfigWorkDirMaintenance.MigrateLegacyWorkDirs();
        // runtime/staging 属可重建暂存区，启动时清掉上次进程的残留临时文件。
        ConfigWorkDirMaintenance.SweepRuntimeStaging();
        if (Limits.Fatals.Count > 0)
        {
            foreach (string fatal in Limits.Fatals)
            {
                Logger.Fatal(fatal);
                Console.Error.WriteLine(fatal);
            }
            Console.Error.WriteLine("约束配置存在致命错误，拒绝启动。请修正 config/limits.json 后重试。");
            return 1;
        }
        foreach (string warning in Limits.Warnings)
        {
            Logger.Warn(warning);
        }
        return 0;
    }

    private static bool IsTestHost()
    {
#if NEXUS_TEST_HOST
        return true;
#else
        return false;
#endif
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

}
