using NexusPipeline.App.Repositories;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Services.Update;
using NexusPipeline.Utilities;

namespace NexusPipeline;

/// <summary>
/// 应用运行时初始化：权限契约、旧配置迁移、约束加载以及共享数据加载。
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
        MigrateLegacyConfig();
        try
        {
            // 必须在新模型加载和 ConfigSwap 崩溃恢复之前完成全局用户迁移。
            UserModelMigration.EnsureMigrated();
        }
        catch (Exception ex)
        {
            Logger.Fatal($"[致命] v0.9.6 全局用户迁移失败，已拒绝启动：{ex.Message}");
            Console.Error.WriteLine($"[FATAL] v0.9.6 全局用户迁移失败，已拒绝启动：{ex.Message}");
            return 1;
        }
        // 先加载约束，再加载设置（Normalize 使用固定的历史保留天数上限）。
        Limits.Load();
        RuntimeContext ctx = RuntimeContext.Instance;
        ctx.ReloadSettings();
        ctx.ReloadData();
        // 配置交换恢复的数据源由组合根装配（恢复路径不再反向依赖 RuntimeContext；
        // 所有进程模式共用，service/web 的 StartupPipeline 启动恢复与 CLI 运行时自愈均由此覆盖）。
        ConfigSwapSession.ConfigureRecovery(new RuntimeConfigRecoveryDataSource(ctx.FindScript, ctx.SnapshotUsers));
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

    private static void MigrateLegacyConfig()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ConfigDir);
            var pairs = new[]
            {
                (Legacy: Path.Combine(AppPaths.AppRoot, "settings.json"), New: AppPaths.ConfigPath, Name: "settings.json"),
                (Legacy: Path.Combine(AppPaths.AppRoot, "scripts.json"), New: AppPaths.ScriptsPath, Name: "scripts.json"),
                (Legacy: Path.Combine(AppPaths.AppRoot, "queues.json"), New: AppPaths.QueuesPath, Name: "queues.json"),
            };
            foreach ((string legacy, string dest, string name) in pairs)
            {
                if (File.Exists(legacy) && !File.Exists(dest))
                {
                    File.Move(legacy, dest);
                    Audit.Log(Audit.System, "迁移旧配置文件", $"{name} → config\\{name}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 迁移旧配置文件失败：{ex.Message}");
        }
    }
}
