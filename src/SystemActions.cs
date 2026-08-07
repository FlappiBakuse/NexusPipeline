using System.Diagnostics;
using System.Text;

namespace NexusPipeline;

internal static class SystemActions
{
    public static bool IsCommandFile(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() is ".bat" or ".cmd" or ".com";
    }

    /// <summary>
    /// 构建脚本启动信息：.bat/.cmd/.com 一律经 cmd.exe /d /s /c 包装（UseShellExecute=false 的 CreateProcess 路径），
    /// 完全规避 ShellExecute 对批处理文件的关联启动（避免系统“出现错误 0x800700E8”弹窗）；exe 直接启动。
    /// </summary>
    public static ProcessStartInfo BuildScriptStartInfo(string exePath, string workingDir, IEnumerable<string> args, bool noWindow, bool redirect)
    {
        if (IsCommandFile(exePath))
        {
            var sb = new StringBuilder();
            sb.Append("/d /s /c \"\"").Append(exePath).Append('"');
            foreach (string arg in args)
            {
                sb.Append(" \"").Append(arg.Replace("\"", "\\\"")).Append('"');
            }
            sb.Append('"');
            return new ProcessStartInfo("cmd.exe", sb.ToString())
            {
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = noWindow,
                RedirectStandardOutput = redirect,
                RedirectStandardError = redirect,
            };
        }
        var psi = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = noWindow,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        return psi;
    }

    /// <summary>启动进程并持续消费已重定向的标准输出，避免子进程因管道写满而阻塞。</summary>
    public static Process? StartWithOutputDrain(ProcessStartInfo psi, bool disposeWhenExited = false)
    {
        Process? process = Process.Start(psi);
        if (process is not null)
        {
            BeginOutputDrain(process, psi);
            if (disposeWhenExited)
            {
                process.Exited += (_, _) => process.Dispose();
                process.EnableRaisingEvents = true;
            }
        }
        return process;
    }

    private static void BeginOutputDrain(Process process, ProcessStartInfo psi)
    {
        if (psi.RedirectStandardOutput)
        {
            process.OutputDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
        }
        if (psi.RedirectStandardError)
        {
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginErrorReadLine();
        }
    }

    /// <summary>
    /// 可见窗口启动（编辑配置模式）。所有路径都通过 CreateProcess + 重定向管道启动：
    /// 无控制台父进程也能为 cmd.exe、PowerShell 等控制台程序提供有效 stdio，避免 ERROR_NO_DATA(0x800700E8)。
    /// 批处理仅作为启动器静默运行，直接启动的编辑器保留可见窗口。
    /// </summary>
    public static Process? StartVisible(string exePath, string workingDir)
    {
        bool commandFile = IsCommandFile(exePath);
        var psi = BuildScriptStartInfo(exePath, workingDir, Array.Empty<string>(), noWindow: commandFile, redirect: true);
        try
        {
            return StartWithOutputDrain(psi);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"程序启动失败（{exePath}）：{ex.Message}", ex);
        }
    }

    public static void KillTree(int pid)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("taskkill.exe", $"/PID {pid} /T /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit(10000);
            if (process is not null && process.ExitCode == 0)
            {
                Logger.Info($"已清理进程树（PID {pid}）。");
            }
            else
            {
                Logger.Warn($"[警告] 进程树清理返回码 {process?.ExitCode}（PID {pid}）。");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 进程树清理失败（PID {pid}）：{ex.Message}");
        }
    }

    public static void KillByName(string exeName, string display)
    {
        if (string.IsNullOrWhiteSpace(exeName))
        {
            return;
        }
        try
        {
            string baseName = Path.GetFileNameWithoutExtension(exeName);
            Process[] processes = Process.GetProcessesByName(baseName);
            if (processes.Length == 0)
            {
                Logger.Info($"[提示] 未发现需要关闭的{display}进程（{baseName}）。");
                return;
            }
            foreach (Process process in processes)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[警告] 关闭{display}进程失败（PID {process.Id}）：{ex.Message}");
                }
            }
            Logger.Info($"已强制关闭{display}（{baseName}，共 {processes.Length} 个进程）。");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 按名称关闭{display}进程失败：{ex.Message}");
        }
    }

    public static void Shutdown(int delaySeconds = 60)
    {
        Run("shutdown.exe", $"/s /t {delaySeconds} /c \"NexusPipeline 队列已完成，自动关机\"");
    }

    public static void Reboot(int delaySeconds = 60)
    {
        Run("shutdown.exe", $"/r /t {delaySeconds} /c \"NexusPipeline 队列已完成，自动重启\"");
    }

    public static void Hibernate()
    {
        Run("shutdown.exe", "/h");
    }

    public static void ExitApp()
    {
        Logger.Info("调度队列完成操作：退出软件。");
        try
        {
            System.Windows.Forms.Application.Exit();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 退出软件失败：{ex.Message}");
        }
    }

    private static void Run(string file, string args)
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
            }
            else
            {
                Logger.Warn($"[警告] 系统操作命令返回码 {process?.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 系统操作命令执行失败：{ex.Message}");
        }
    }
}
