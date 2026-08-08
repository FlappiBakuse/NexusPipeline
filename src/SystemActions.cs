using System.Diagnostics;
using System.Text;

namespace NexusPipeline;

internal static class SystemActions
{
    public static bool IsCommandFile(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() is ".bat" or ".cmd" or ".com";
    }

    /// <summary>是否为「请求的操作需要提升」（ERROR_ELEVATION_REQUIRED，目标程序 manifest 要求管理员权限）。</summary>
    public static bool IsElevationRequired(Exception ex)
    {
        return ex is System.ComponentModel.Win32Exception { NativeErrorCode: 740 };
    }

    /// <summary>
    /// 以提升权限启动（ShellExecute Verb=runas）：目标程序 manifest 要求管理员权限时的兜底方案，
    /// 不依赖宿主进程权限；UAC 从不通知 + 管理员账户时直接提权无弹窗，标准用户弹凭据确认。
    /// 提权路径不可重定向输出（GUI 程序无碍，运行判定依赖日志文件）。
    /// </summary>
    public static Process? StartWithElevation(string exePath, string workingDir, IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        foreach (string arg in args)
        {
            sb.Append(" \"").Append(arg.Replace("\"", "\\\"")).Append('"');
        }
        var psi = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = true,
            Verb = "runas",
        };
        if (sb.Length > 0)
        {
            psi.Arguments = sb.ToString().TrimStart();
        }
        return Process.Start(psi);
    }

    /// <summary>
    /// 解析脚本自启动参数是否为「运行时启动目标 + 参数」（管理端/执行端分离场景）。
    /// 仅当 Args 以显式路径特征开头（盘符 X:\、UNC \\、.\ 或 ..\）时按此语义处理：
    /// 整段到「?」为止为启动目标路径（路径段去除尾随空格），相对工作目录（脚本根目录）按标准 Windows 相对路径语义解析，
    /// 含空格无需引号；「?」之后按普通参数规则拆分为启动目标参数（无「?」则无参数）。
    /// 引号一律视为普通参数内容（不在参数里使用引号包裹路径，避免歧义）。
    /// 其余情况（普通参数开头）原样全部传给主程序；路径解析失败回退主程序并警告。
    /// </summary>
    public static (string ExePath, List<string> Args) ResolveLaunchTarget(string mainExe, string workingDir, string argsText)
    {
        if (string.IsNullOrWhiteSpace(argsText))
        {
            return (mainExe, new List<string>());
        }
        string trimmed = argsText.Trim();
        bool absolute = (trimmed.Length >= 3 && char.IsLetter(trimmed[0]) && trimmed[1] == ':' && (trimmed[2] == '\\' || trimmed[2] == '/'))
            || trimmed.StartsWith("\\\\", StringComparison.Ordinal);
        bool relative = trimmed.StartsWith(".\\", StringComparison.Ordinal)
            || trimmed.StartsWith("./", StringComparison.Ordinal)
            || trimmed.StartsWith("..\\", StringComparison.Ordinal)
            || trimmed.StartsWith("../", StringComparison.Ordinal);
        if (!absolute && !relative)
        {
            return (mainExe, TextRules.SplitArgs(argsText));
        }
        string targetPart = trimmed;
        string targetArgsPart = "";
        int question = trimmed.IndexOf('?');
        if (question >= 0)
        {
            targetPart = trimmed[..question].TrimEnd();
            targetArgsPart = trimmed[(question + 1)..].Trim();
        }
        string candidate;
        try
        {
            candidate = absolute ? targetPart : Path.GetFullPath(Path.Combine(workingDir, targetPart));
        }
        catch (Exception)
        {
            return (mainExe, TextRules.SplitArgs(argsText));
        }
        if (TextRules.IsExecutable(candidate))
        {
            if (!string.IsNullOrWhiteSpace(targetPart))
            {
                Logger.Info($"[解析] 脚本自启动参数为显式路径，运行时启动目标改为：{candidate}");
            }
            return (candidate, TextRules.SplitArgs(targetArgsPart));
        }
        Logger.Warn($"[警告] 脚本自启动参数含显式路径但无法解析为可执行文件（{candidate}），按普通参数传给主程序。");
        return (mainExe, TextRules.SplitArgs(argsText));
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
        catch (Exception ex) when (IsElevationRequired(ex))
        {
            Logger.Info($"[提示] 程序需要管理员权限，以提升权限（runas）启动：{exePath}");
            try
            {
                return StartWithElevation(exePath, workingDir, Array.Empty<string>());
            }
            catch (System.ComponentModel.Win32Exception ex2) when (ex2.NativeErrorCode == 1223)
            {
                throw new InvalidOperationException($"程序启动失败（{exePath}）：需要管理员权限，用户取消了提权确认", ex2);
            }
            catch (Exception ex2)
            {
                throw new InvalidOperationException($"程序启动失败（{exePath}）：需要管理员权限，提权重试失败：{ex2.Message}", ex2);
            }
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
            else if (process is not null && process.ExitCode == 128)
            {
                Logger.Info($"进程树无需清理（PID {pid} 已不存在）。");
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

    /// <summary>
    /// 检测指定可执行程序是否已有进程在运行（编辑配置/运行前的防冲突检查）。
    /// 按进程名（不含扩展名，不区分大小写）检测：覆盖程序从其他目录/副本或提升权限运行等全路径比对不可靠的场景；
    /// 同名无关进程可能误报（可接受的权衡）；批处理等经 cmd 包装的脚本不产生同名进程，无法按名检测，保持放行。
    /// </summary>
    public static bool IsExeRunning(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }
        string baseName = Path.GetFileNameWithoutExtension(exePath);
        if (baseName.Length == 0)
        {
            return false;
        }
        try
        {
            return Process.GetProcessesByName(baseName).Length > 0;
        }
        catch
        {
            return false;
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
