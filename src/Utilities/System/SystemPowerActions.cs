using System.Diagnostics;

namespace NexusPipeline.Utilities;

internal static class SystemPowerActions
{
    /// <summary>
    /// 关机（Windows 自带 60 秒倒计时，可 shutdown /a 取消）。
    /// NEXUS_SYSTEM_ACTION_DRYRUN=1（e2e 全局设置）时不真正执行，仅记录日志（CI 绝不真关机）。
    /// </summary>
    public static void Shutdown(int delaySeconds = 60)
    {
        if (DryRun())
        {
            Logger.Info($"[DRYRUN] 系统操作（已抑制执行）：shutdown.exe /s /t {delaySeconds}");
            return;
        }
        Run("shutdown.exe", $"/s /t {delaySeconds} /c \"NexusPipeline 队列已完成，自动关机\"");
    }

    /// <summary>重启（Windows 自带 60 秒倒计时，可 shutdown /a 取消）；DRYRUN 语义同 <see cref="Shutdown"/>。</summary>
    public static void Reboot(int delaySeconds = 60)
    {
        if (DryRun())
        {
            Logger.Info($"[DRYRUN] 系统操作（已抑制执行）：shutdown.exe /r /t {delaySeconds}");
            return;
        }
        Run("shutdown.exe", $"/r /t {delaySeconds} /c \"NexusPipeline 队列已完成，自动重启\"");
    }

    /// <summary>休眠（立即执行，无系统倒计时）；DRYRUN 语义同 <see cref="Shutdown"/>。</summary>
    public static void Hibernate()
    {
        if (DryRun())
        {
            Logger.Info("[DRYRUN] 系统操作（已抑制执行）：shutdown.exe /h");
            return;
        }
        Run("shutdown.exe", "/h");
    }

    /// <summary>取消 Windows 关机/重启倒计时（shutdown /a；无倒计时时是无害空操作， 取消完成操作卡片用）。</summary>
    public static bool CancelShutdown()
    {
        if (DryRun())
        {
            Logger.Info("[DRYRUN] 系统操作（已抑制执行）：shutdown.exe /a");
            return true;
        }
        return Run("shutdown.exe", "/a");
    }

    /// <summary>测试闸门：NEXUS_SYSTEM_ACTION_DRYRUN=1 时抑制真实系统操作（e2e 在 global-setup 设置，服务进程继承）。</summary>
    private static bool DryRun()
    {
        return Environment.GetEnvironmentVariable("NEXUS_SYSTEM_ACTION_DRYRUN") == "1";
    }

    public static void ExitApp()
    {
        Logger.Info("调度队列完成操作：退出软件。");
        try
        {
            NexusPipeline.Bootstrap.TryRequestCompletionExit();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 退出软件失败：{ex.Message}");
        }
    }

    private static bool Run(string file, string args)
    {
        Logger.Info($"执行系统操作：{file} {args}");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit(10000);
            if (process is not null && process.ExitCode == 0)
            {
                Logger.Info("系统操作命令已提交。");
                return true;
            }
            Logger.Warn($"[警告] 系统操作命令返回码 {process?.ExitCode}");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 系统操作命令执行失败：{ex.Message}");
            return false;
        }
    }

}
