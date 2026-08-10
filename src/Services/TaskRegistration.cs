using System.Diagnostics;
using NexusPipeline.Models;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>开机自启动：计划任务（schtasks /sc onlogon /rl highest），登录时以最高权限静默启动（免 UAC 弹窗），与提权版主程序配套。</summary>
internal static class TaskRegistration
{
    private const string TaskName = "NexusPipeline";

    public static bool IsRegistered()
    {
        return RunSchTask("/query", "/tn", TaskName).ExitCode == 0;
    }

    public static void Register()
    {
        try
        {
            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrWhiteSpace(exePath))
            {
                Logger.Error("[错误] 无法确定主程序路径。");
                return;
            }
            var result = RunSchTask("/create", "/tn", TaskName, "/tr", $"\\\"{exePath}\\\"", "/sc", "onlogon", "/rl", "highest", "/f");
            if (result.ExitCode == 0)
            {
                Audit.Log(Audit.System, "注册开机自启动（计划任务，最高权限）", exePath);
                Logger.Info($"[提示] 开机自启动已注册为计划任务（{TaskName}，登录时以最高权限运行）。");
            }
            else
            {
                Logger.Error($"[错误] 注册开机自启动失败（schtasks 退出码 {result.ExitCode}）：{result.Output.Trim()}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[错误] 注册开机自启动失败：{ex.Message}");
        }
    }

    public static void Unregister()
    {
        try
        {
            var result = RunSchTask("/delete", "/tn", TaskName, "/f");
            if (result.ExitCode == 0)
            {
                Audit.Log(Audit.System, "取消开机自启动");
            }
            else if (IsRegistered())
            {
                Logger.Error($"[错误] 取消开机自启动失败（schtasks 退出码 {result.ExitCode}）：{result.Output.Trim()}");
            }
            else
            {
                Logger.Info("[提示] 未注册开机自启动。");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[错误] 取消开机自启动失败：{ex.Message}");
        }
    }

    public static void SyncWithSettings(AppSettings settings)
    {
        if (settings.AutoStart)
        {
            if (!IsRegistered())
            {
                Register();
            }
        }
        else if (IsRegistered())
        {
            Unregister();
        }
    }

    /// <summary>调用 schtasks.exe（控制台程序，无控制台父进程须重定向 stdio，避免 0x800700E8）。</summary>
    private static (int ExitCode, string Output) RunSchTask(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        using Process? process = Process.Start(psi);
        if (process is null)
        {
            return (-1, "无法创建 schtasks 进程");
        }
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(15000);
        return (process.ExitCode, output);
    }
}
