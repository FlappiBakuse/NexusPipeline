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
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 脚本自启动参数含显式路径但解析异常，按普通参数传给主程序：{ex.Message}");
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

    /// <summary>
    /// 清理进程树（v0.6.5+ 自实现）：Toolhelp 快照枚举父子关系后 BFS 遍历，逐进程 taskkill /F（不带 /T）。
    /// excludeProcessBaseName 非空时（与 GameExe 同名的进程名，不含扩展名、忽略大小写）跳过该进程整棵子树——
    /// 脚本自启动的游戏进程即使父进程是脚本，只要进程名与游戏配置一致就视为「游戏进程」而非脚本树成员，
    /// 其生杀归游戏管理（ForceCloseGame / 失败路径按名关闭），不被脚本进程树连带清理。
    /// 快照失败回退原 taskkill /T 全树语义（宁可不残留，日志 Warn 提示）。
    /// </summary>
    public static void KillTree(int pid, string? excludeProcessBaseName = null)
    {
        try
        {
            IReadOnlyDictionary<int, ProcessNode> nodes = SnapshotProcesses();
            if (!nodes.ContainsKey(pid))
            {
                Logger.Info($"进程树无需清理（PID {pid} 已不存在）。");
                return;
            }
            HashSet<int> targets = CollectTree(pid, nodes, excludeProcessBaseName);
            int killed = 0;
            foreach (int target in targets)
            {
                if (KillProcess(target))
                {
                    killed++;
                }
            }
            if (targets.Count == 0)
            {
                // v0.7.4（KN-28）：根进程仍存在但树为空——可能被排除进程名（游戏）跳过，文案不再误称「PID 已不存在」。
                Logger.Info($"进程树无需清理（PID {pid} 下无待清理进程，或进程与排除名单同名被跳过）。");
            }
            else if (killed == targets.Count)
            {
                Logger.Info($"已清理进程树（PID {pid}，共 {killed} 个进程）。");
            }
            else
            {
                Logger.Warn($"[警告] 进程树清理部分失败（PID {pid}，成功 {killed}/{targets.Count}）。");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 进程树清理失败（PID {pid}）：{ex.Message}，回退全树清理。");
            FallbackKillTree(pid);
        }
    }

    /// <summary>回退方案：taskkill /T 递归全树（快照失败时使用，不排除游戏进程）。</summary>
    private static void FallbackKillTree(int pid)
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

    internal sealed record ProcessNode(int Pid, int Ppid, string ExeName);

    /// <summary>Toolhelp 快照全部进程（PID/父 PID/映像名）；失败抛异常由调用方回退。</summary>
    private static IReadOnlyDictionary<int, ProcessNode> SnapshotProcesses()
    {
        var nodes = new Dictionary<int, ProcessNode>();
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE)
        {
            throw new InvalidOperationException($"进程快照失败（错误码 {Marshal.GetLastWin32Error()}）");
        }
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    nodes[(int)entry.th32ProcessID] = new ProcessNode((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile);
                }
                while (Process32Next(snapshot, ref entry));
            }
            return nodes;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    /// <summary>从根 PID BFS 收集进程树；excludeBaseName 匹配的节点跳过且不扩展其子树（internal 供单元测试验证纯逻辑）。</summary>
    internal static HashSet<int> CollectTree(int rootPid, IReadOnlyDictionary<int, ProcessNode> nodes, string? excludeBaseName)
    {
        var result = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);
        while (queue.Count > 0)
        {
            int pid = queue.Dequeue();
            if (result.Contains(pid) || !nodes.TryGetValue(pid, out ProcessNode? node))
            {
                continue;
            }
            if (excludeBaseName is not null && IsSameProcessName(node.ExeName, excludeBaseName))
            {
                continue;
            }
            result.Add(pid);
            foreach ((int childPid, ProcessNode child) in nodes)
            {
                if (child.Ppid == pid)
                {
                    queue.Enqueue(childPid);
                }
            }
        }
        return result;
    }

    private static bool IsSameProcessName(string exeFile, string baseName)
    {
        try
        {
            return string.Equals(Path.GetFileNameWithoutExtension(exeFile), baseName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.Warn($"进程名比对失败，按非游戏进程处理（{exeFile}）：{ex.Message}");
            return false;
        }
    }

    /// <summary>单个进程强制结束（taskkill /F，不带 /T）。</summary>
    private static bool KillProcess(int pid)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("taskkill.exe", $"/PID {pid} /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit(10000);
            return process is not null && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;

        public uint cntUsage;

        public uint th32ProcessID;

        public IntPtr th32DefaultHeapID;

        public uint th32ModuleID;

        public uint cntThreads;

        public uint th32ParentProcessID;

        public int pcPriClassBase;

        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll")]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

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
        catch (Exception ex)
        {
            Logger.Warn($"进程检测失败（{exePath}），按未运行处理：{ex.Message}");
            return false;
        }
    }

    public static void KillByName(string exeName, string display, string? excludeProcessBaseName = null)
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
                    if (excludeProcessBaseName is not null)
                    {
                        // v0.7.5（台账外）：按名清理携带排除名单时走 Toolhelp 树清理——此前 Process.Kill(entireProcessTree: true)
                        // 会把脚本自启动的游戏子孙进程一并杀死，与「游戏进程不属脚本树、生杀归游戏管理」的声明不一致。
                        KillTree(process.Id, excludeProcessBaseName);
                    }
                    else
                    {
                        process.Kill(entireProcessTree: true);
                    }
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
    /// 返回 true=已确认退出；false=轮数耗尽后仍在运行（疑似持续自重启，调用方应拒绝执行依赖该进程退出的后续动作）。
    /// excludeProcessBaseName（v0.6.5+）：与 GameExe 同名的进程不视为脚本树成员（见 <see cref="KillTree"/>），
    /// 游戏进程由游戏管理逻辑（ForceCloseGame/失败路径按名关闭）处理。
    /// </summary>
    public static bool KillAndConfirmExited(int pid, string exePath, string display, int rounds = 5, int intervalMs = 800, string? excludeProcessBaseName = null)
    {
        KillTree(pid, excludeProcessBaseName);
        for (int round = 1; round <= rounds; round++)
        {
            if (!IsExeRunning(exePath))
            {
                return true;
            }
            Logger.Info($"[提示] {display}进程仍在运行（第 {round}/{rounds} 轮按名清理，含自重启产物）。");
            // v0.7.5（台账外）：自重启轮按名清理同样携带排除名单（游戏名），避免 Process.Kill 全树连带杀死游戏子孙进程。
            KillByName(exePath, display, excludeProcessBaseName);
            if (round < rounds)
            {
                Thread.Sleep(intervalMs);
            }
        }
        if (IsExeRunning(exePath))
        {
            Logger.Warn($"[警告] {display}进程清理后仍在运行（疑似持续自重启），请手动检查：{exePath}");
            return false;
        }
        return true;
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
    /// 后台前置进程窗口（v0.6.5+，仅启动时一次）：fire-and-forget 但观察异常（宿主为常驻服务进程，P/Invoke 均在后台线程执行）。
    /// 编辑用户配置（主程序窗口前置）与运行脚本实例/调度队列（游戏窗口前置）共用。
    /// </summary>
    public static void BringToFrontFireAndForget(int pid, string what)
    {
        if (pid <= 0)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                BringToFront(pid);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 前置{what}窗口失败：{ex.Message}");
            }
        });
    }

    /// <summary>
    /// 后台最小化进程窗口（v0.6.5+，仅启动时一次）：fire-and-forget 但观察异常。
    /// 运行脚本实例/调度队列时脚本主窗口最小化让位（命令行/日志已接管输出），游戏窗口前置以利截图识别。
    /// </summary>
    public static void MinimizeWindowFireAndForget(int pid, string what)
    {
        if (pid <= 0)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                MinimizeWindow(pid);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 最小化{what}窗口失败：{ex.Message}");
            }
        });
    }

    /// <summary>
    /// 将指定进程的可见主窗口前置（v0.6.5+ 强化）：轮询进程顶层可见窗口（EnumWindows 按 PID 匹配），
    /// 找到后组合前置——还原最小化 + AttachThreadInput 模拟前台线程输入（绕过 Windows 前台锁定，
    /// 后台常驻服务进程直接 SetForegroundWindow 几乎必然失败）+ BringWindowToTop 置顶 Z 序 + SetForegroundWindow 激活；
    /// 前置失败（前台被其他窗口占据/窗口尚未就绪）每 1 秒重试，直至成功或超时。
    /// 用于游戏窗口/编辑配置主程序启动后避免被浏览器等前台窗口遮挡（如 BetterGI 截图识别游戏画面需要窗口在最前）。
    /// 找不到可见窗口（bat/cmd 无窗口、进程无窗口）静默放弃；超时仍失败输出 Warn 日志（可观测）。
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
            if (hWnd != IntPtr.Zero && TryBringToFront(hWnd))
            {
                Logger.Debug($"[前置] 已前置进程窗口（PID {pid}，句柄 {hWnd}）。");
                return true;
            }
            Thread.Sleep(1000);
        }
        Logger.Warn($"[警告] 前置进程窗口超时（PID {pid}，{timeoutSeconds} 秒内未能置顶），窗口可能被其他界面遮挡。");
        return false;
    }

    /// <summary>
    /// 将指定进程的可见主窗口最小化（v0.6.5+）：轮询窗口出现后 ShowWindow(SW_MINIMIZE)（GUI 脚本让位，
    /// 控制台脚本经 cmd 包装已无窗口，静默跳过）。用于运行脚本实例/调度队列时脚本主窗口最小化。
    /// </summary>
    public static bool MinimizeWindow(int pid, int timeoutSeconds = 30)
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
                ShowWindow(hWnd, SW_MINIMIZE);
                Logger.Debug($"[最小化] 已最小化进程窗口（PID {pid}，句柄 {hWnd}）。");
                return true;
            }
            Thread.Sleep(300);
        }
        Logger.Debug($"[最小化] 未找到进程可见窗口（PID {pid}），跳过。");
        return false;
    }

    /// <summary>组合前置单次尝试：还原最小化 → 附加前台线程输入（绕过前台锁定）→ 置顶 → 激活。返回 SetForegroundWindow 是否成功。</summary>
    private static bool TryBringToFront(IntPtr hWnd)
    {
        ShowWindow(hWnd, SW_RESTORE);
        ShowWindow(hWnd, SW_SHOW);
        IntPtr foreground = GetForegroundWindow();
        uint targetThread = GetWindowThreadProcessId(hWnd, out _);
        bool attached = false;
        if (foreground != IntPtr.Zero)
        {
            uint fgThread = GetWindowThreadProcessId(foreground, out _);
            if (fgThread != targetThread)
            {
                attached = AttachThreadInput(fgThread, targetThread, true);
            }
        }
        try
        {
            BringWindowToTop(hWnd);
            bool ok = SetForegroundWindow(hWnd);
            if (ok)
            {
                SetFocus(hWnd);
                SetActiveWindow(hWnd);
            }
            return ok;
        }
        finally
        {
            if (attached)
            {
                uint fgThread = GetWindowThreadProcessId(foreground, out _);
                AttachThreadInput(fgThread, targetThread, false);
            }
        }
    }

    private const int SW_RESTORE = 9;

    private const int SW_MINIMIZE = 6;

    private const int SW_SHOW = 5;

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

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

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
