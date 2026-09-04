using System.Diagnostics;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>远程访问防火墙入站规则管理：开启远程访问时确保 Web TCP 端口的入站允许规则存在。</summary>
internal static class FirewallRule
{
    // HttpListener 的入站连接由 Windows HTTP.sys 接收；端口规则不依赖防火墙将连接归属到用户态 exe。
    // 保留旧版 "NexusPipeline Web" 程序规则，新规则使用独立名称以便平滑升级并可按实际端口更新。
    private const string RuleName = "NexusPipeline Web TCP";

    /// <summary>确保实际 Web 端口存在 TCP 入站允许规则（幂等，支持端口漂移）。</summary>
    public static void EnsureAllowInbound(int port)
    {
        if (port is < 1 or > 65535)
        {
            Logger.Warn($"[防火墙] 忽略无效 Web 端口 {port}，未更新入站规则。");
            return;
        }
        try
        {
            string setArgs = $"advfirewall firewall set rule name=\"{RuleName}\" new dir=in action=allow protocol=TCP localport={port} enable=yes profile=private,public";
            int code = RunNetsh(setArgs);
            if (code != 0)
            {
                string addArgs = $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} enable=yes profile=private,public";
                code = RunNetsh(addArgs);
            }
            if (code == 0)
            {
                Logger.Info($"[防火墙] 已确保 NexusPipeline Web TCP 入站允许规则（端口 {port}，Private/Public）。");
            }
            else
            {
                Logger.Warn($"[防火墙] 更新 Web TCP 入站规则返回码 {code}（不影响本地使用，局域网设备可能无法访问）。");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[防火墙] 更新 Web TCP 入站规则失败：{ex.Message}");
        }
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
