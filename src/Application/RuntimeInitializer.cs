using NexusPipeline.Services.Update;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline;

/// <summary>
/// 应用公共初始化：权限契约、约束加载以及只读设置快照。
/// 该阶段不加载、不修复 Scripts / Queues / Users，也不启动服务。
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
        ctx.ReloadSettings(Persistence.ConfigLoadMode.ReadOnly);
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
