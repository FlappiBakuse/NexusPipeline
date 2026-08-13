using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NexusPipeline.Utilities;

internal static class SystemActions
{
    public static bool IsCommandFile(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() is ".bat" or ".cmd" or ".com";
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
    /// NexusPipeline 已强制以管理员身份运行，目标程序要求管理员权限时（740）直接报错，不再降级提权。
    /// </summary>
    public static Process? StartVisible(string exePath, string workingDir)
    {
        bool commandFile = IsCommandFile(exePath);
        var psi = BuildScriptStartInfo(exePath, workingDir, Array.Empty<string>(), noWindow: commandFile, redirect: true);
        try
        {
            return StartWithOutputDrain(psi);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            throw new InvalidOperationException($"程序启动失败（{exePath}）：目标程序要求管理员权限，但 NexusPipeline 已以管理员身份运行仍被拒绝，请检查目标程序的权限配置", ex);
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

    /// <summary>
    /// 清理脚本进程并确认退出（v0.6.0+）：进程树清理后轮询同名进程，处理「被杀后自重启」的脚本
    /// （如 BetterGI 防崩溃机制，日志曾出现强杀两轮才干净）——每轮仍存在则按名强杀，直至确认退出或轮数耗尽。
    /// 确保配置交换还原前脚本进程已完全退出，消除文件占用导致的还原失败窗口。
    /// </summary>
    public static void KillAndConfirmExited(int pid, string exePath, string display, int rounds = 5, int intervalMs = 800)
    {
        KillTree(pid);
        for (int round = 1; round <= rounds; round++)
        {
            if (!IsExeRunning(exePath))
            {
                return;
            }
            Logger.Info($"[提示] {display}进程仍在运行（第 {round}/{rounds} 轮按名清理，含自重启产物）。");
            KillByName(exePath, display);
            if (round < rounds)
            {
                Thread.Sleep(intervalMs);
            }
        }
        if (IsExeRunning(exePath))
        {
            Logger.Warn($"[警告] {display}进程清理后仍在运行（疑似持续自重启），请手动检查：{exePath}");
        }
    }

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

    /// <summary>取消 Windows 关机/重启倒计时（shutdown /a；无倒计时时是无害空操作，v0.6.3 取消完成操作卡片用）。</summary>
    public static void CancelShutdown()
    {
        Run("shutdown.exe", "/a");
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
            System.Windows.Forms.Application.Exit();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 退出软件失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 将指定进程的可见主窗口前置（仅启动时一次，v0.6.0+）：轮询进程顶层可见窗口（EnumWindows 按 PID 匹配），
    /// 找到后还原最小化状态并 SetForegroundWindow 前置。用于运行脚本/游戏启动后避免被其他界面遮挡
    /// （如 BetterGI 截图识别游戏画面需要窗口可见）。找不到可见窗口（bat/cmd 无窗口、进程无窗口）静默放弃。
    /// </summary>
    public static bool BringToFront(int pid, int timeoutSeconds = 30)
    {
        if (pid <= 0)
        {
            return false;
        }
        DateTime deadline = DateTime.Now.AddSeconds(timeoutSeconds);
        while (DateTime.Now < deadline)
        {
            IntPtr hWnd = FindVisibleWindow(pid);
            if (hWnd != IntPtr.Zero)
            {
                ShowWindow(hWnd, SW_RESTORE);
                SetForegroundWindow(hWnd);
                Logger.Debug($"[前置] 已前置进程窗口（PID {pid}，句柄 {hWnd}）。");
                return true;
            }
            Thread.Sleep(300);
        }
        Logger.Debug($"[前置] 未找到进程可见窗口（PID {pid}），跳过。");
        return false;
    }

    private const int SW_RESTORE = 9;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static IntPtr FindVisibleWindow(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid == (uint)pid && IsWindowVisible(hWnd))
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
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
