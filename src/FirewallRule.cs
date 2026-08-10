using System.Diagnostics;

namespace NexusPipeline;

/// <summary>远程访问防火墙入站规则管理：开启远程访问时确保 exe 的入站允许规则存在（Windows 防火墙默认阻止入站）。</summary>
internal static class FirewallRule
{
    private const string RuleName = "NexusPipeline Web";

    /// <summary>确保入站允许规则存在（幂等）：先查后建；任何失败仅告警不阻断（管理员身份下 netsh 可用）。</summary>
    public static void EnsureAllowInbound()
    {
        try
        {
            if (RuleExists())
            {
                return;
            }
            string exe = Path.Combine(AppPaths.AppRoot, "nexus-pipeline.exe");
            string args = $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{exe}\" enable=yes profile=private,public";
            int code = RunNetsh(args);
            if (code == 0)
            {
                Logger.Info($"[防火墙] 已添加 NexusPipeline 入站允许规则（{exe}）。");
            }
            else
            {
                Logger.Warn($"[防火墙] 添加入站规则返回码 {code}（不影响本地使用，局域网设备可能无法访问）。");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[防火墙] 添加入站规则失败：{ex.Message}");
        }
    }

    private static bool RuleExists()
    {
        int code = RunNetsh($"advfirewall firewall show rule name=\"{RuleName}\"");
        return code == 0;
    }

    private static int RunNetsh(string args)
    {
        using var process = Process.Start(new ProcessStartInfo("netsh.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (process is null)
        {
            return -1;
        }
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit(15000);
        return process.HasExited ? process.ExitCode : -1;
    }
}
