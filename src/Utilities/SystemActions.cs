using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NexusPipeline.Utilities;

internal static class SystemActions
{
    // 恢复/配置替换至少要跨过专项 harness 覆盖的 0ms、100ms、500ms、1s、3s、5s
    // 自重启窗口，并留出一次采样抖动余量；测试环境仍通过 TestHooks 缩放墙钟等待。
    internal const int StableExitSeconds = 6;

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

    /// <summary>启动宿主拥有的进程并立即加入本次 Attempt 的 Job Object。</summary>
    public static Process? StartOwnedProcess(ProcessStartInfo psi, ProcessOwnership? ownership)
    {
        Process? process = Process.Start(psi);
        if (process is not null)
        {
            ownership?.TryAssign(process);
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
    /// 无控制台父进程也能为 cmd.exe 等控制台程序提供有效 stdio，避免 ERROR_NO_DATA(0x800700E8)。
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
    /// 清理进程树（自实现）：Toolhelp 快照枚举父子关系后 BFS 遍历，逐进程 taskkill /F（不带 /T）。
    /// excludeProcessBaseName 非空时（与 GameExe 同名的进程名，不含扩展名、忽略大小写）跳过该进程整棵子树——
    /// 脚本自启动的游戏进程即使父进程是脚本，只要进程名与游戏配置一致就视为「游戏进程」而非脚本树成员，
    /// 其生杀归游戏管理（ForceCloseGame / 失败路径按名关闭），不被脚本进程树连带清理。
    /// 快照失败时无排除名单才允许回退 taskkill /T；带 Game 排除名单时返回未确认，避免误杀游戏。
    /// </summary>
    public static ProcessCleanupResult KillTree(int pid, string? excludeProcessBaseName = null)
    {
        if (pid <= 0)
        {
            return ProcessCleanupResult.Unconfirmed(new[] { pid }, "无效根 PID，禁止使用 PID 0 作为身份清理哨兵");
        }
        try
        {
            IReadOnlyDictionary<int, ProcessNode> nodes = SnapshotProcesses();
            if (!nodes.ContainsKey(pid))
            {
                Logger.Info($"进程树根 PID {pid} 已不存在，无法仅凭 Toolhelp 快照确认脱离子进程。");
                return ProcessCleanupResult.Unconfirmed(new[] { pid }, "根进程已不存在，等待稳定退出窗口或由 Job Object 提供所有权证据");
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
                // 根进程仍存在但树为空——可能被排除进程名（游戏）跳过，文案不再误称「PID 已不存在」。
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
            List<int> remaining = targets.Where(IsProcessAlive).ToList();
            return remaining.Count == 0
                ? ProcessCleanupResult.Confirmed("进程树已确认退出")
                : ProcessCleanupResult.Unconfirmed(remaining, $"仍有 {remaining.Count} 个已知进程存活");
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(excludeProcessBaseName))
            {
                Logger.Warn($"[警告] 进程树快照失败（PID {pid}）：{ex.Message}；当前启用游戏排除名单，拒绝回退全树清理并标记为未确认。");
                return ProcessCleanupResult.Unconfirmed(new[] { pid }, $"Toolhelp 快照失败且启用游戏排除名单：{ex.Message}");
            }
            Logger.Warn($"[警告] 进程树清理失败（PID {pid}）：{ex.Message}，回退全树清理。");
            return FallbackKillTree(pid);
        }
    }

    /// <summary>回退方案：taskkill /T 递归全树（快照失败时使用，不排除游戏进程）。</summary>
    private static ProcessCleanupResult FallbackKillTree(int pid)
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
            return IsProcessAlive(pid)
                ? ProcessCleanupResult.Unconfirmed(new[] { pid }, "taskkill 返回后根进程仍存活")
                : ProcessCleanupResult.Confirmed("taskkill 已确认根进程退出");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 进程树清理失败（PID {pid}）：{ex.Message}");
            return ProcessCleanupResult.Unconfirmed(new[] { pid }, $"taskkill 执行失败：{ex.Message}");
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch
        {
            return true;
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
            // 根 PID 是本次宿主启动得到的 owned root，即便与 GameExe 同名也必须纳入脚本清理；
            // 只有根的后代匹配游戏身份时才排除该分支。
            if (pid != rootPid && excludeBaseName is not null && IsSameProcessName(node.ExeName, excludeBaseName))
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
            if (process is not null && process.ExitCode == 0)
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] taskkill 清理 PID {pid} 失败：{ex.Message}，尝试 Process.Kill。");
        }
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return true;
            }
            process.Kill(entireProcessTree: false);
            process.WaitForExit(5000);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
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
                        // （台账外）：按名清理携带排除名单时走 Toolhelp 树清理——此前 Process.Kill(entireProcessTree: true)
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

    /// <summary>按身份观察稳定退出；一次空采样不作为恢复配置的充分条件。</summary>
    public static bool IsExeStoppedStable(
        string exePath,
        int stableSeconds = StableExitSeconds,
        bool waitIfInitiallyStopped = true)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return true;
        }
        if (!waitIfInitiallyStopped && !IsExeRunning(exePath))
        {
            return true;
        }
        int seconds = Math.Max(1, TestHooks.ScaledSeconds(stableSeconds));
        var window = new StableExitWindow(TimeSpan.FromSeconds(seconds));
        // 允许在稳定窗口内捕获一次延迟重启，并为重启后的新窗口留出完整确认时间。
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds * 2 + 1);
        while (DateTime.UtcNow < deadline)
        {
            if (IsExeRunning(exePath))
            {
                window.Observe(hasOwnedProcess: true, DateTime.UtcNow);
            }
            else if (window.Observe(hasOwnedProcess: false, DateTime.UtcNow))
            {
                return true;
            }
            Thread.Sleep(Math.Max(10, Math.Min(200, TestHooks.ScaledMs(100))));
        }
        return window.IsStable;
    }

    /// <summary>清理本次 Attempt 的 owned tree；Job Object 可在 launcher 退出后继续提供 detached child 证据。</summary>
    public static bool KillOwnedProcessTree(
        ProcessOwnership? ownership,
        int rootPid,
        string exePath,
        string display,
        int rounds = 5,
        int intervalMs = 800,
        string? excludeProcessBaseName = null,
        int? stableSeconds = null)
    {
        if (rootPid <= 0)
        {
            Logger.Warn($"[警告] {display}收到无效 root PID {rootPid}，拒绝执行 owned tree 清理。");
            return false;
        }

        ProcessCleanupResult cleanup = KillOwnedAndExpectedProcesses(ownership, rootPid, exePath, excludeProcessBaseName);
        if (!cleanup.ConfirmedExited)
        {
            Logger.Warn($"[警告] {display}进程树初次清理未确认：{cleanup.Reason}。");
        }
        return ConfirmStableExit(
            exePath,
            display,
            () => KillOwnedAndExpectedProcesses(ownership, rootPid, exePath, excludeProcessBaseName),
            () => CaptureOwnedAndExpectedIdentities(ownership, exePath, excludeProcessBaseName, rootPid),
            rounds,
            intervalMs,
            excludeProcessBaseName,
            cleanup,
            stableSeconds);
    }

    /// <summary>没有 root PID 时的显式身份清理入口，供旧进程/编辑会话恢复使用。</summary>
    public static bool KillExistingProcessesByIdentity(
        string exePath,
        string display,
        int rounds = 5,
        int intervalMs = 800,
        string? excludeProcessBaseName = null,
        int? stableSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return true;
        }
        ProcessCleanupResult initial = KillExpectedIdentityProcesses(exePath, excludeProcessBaseName, rootPid: null);
        return ConfirmStableExit(
            exePath,
            display,
            () => KillExpectedIdentityProcesses(exePath, excludeProcessBaseName, rootPid: null),
            () => CaptureExecutableIdentities(exePath, excludeProcessBaseName, rootPid: null),
            rounds,
            intervalMs,
            excludeProcessBaseName,
            initial,
            stableSeconds);
    }

    private static ProcessCleanupResult KillOwnedAndExpectedProcesses(
        ProcessOwnership? ownership,
        int rootPid,
        string exePath,
        string? excludeProcessBaseName)
    {
        ProcessCleanupResult owned = ownership is not null && ownership.IsUsable
            ? KillOwnedFromJob(ownership, rootPid, excludeProcessBaseName)
            : KillTree(rootPid, excludeProcessBaseName);
        ProcessCleanupResult expected = KillExpectedIdentityProcesses(exePath, excludeProcessBaseName, rootPid);
        return CombineCleanup(owned, expected);
    }

    private static ProcessCleanupResult KillExpectedIdentityProcesses(
        string exePath,
        string? excludeProcessBaseName,
        int? rootPid)
    {
        IReadOnlyList<ProcessIdentity> identities = CaptureExecutableIdentities(exePath, excludeProcessBaseName, rootPid);
        int killed = 0;
        foreach (ProcessIdentity identity in identities)
        {
            if (TryKillIdentity(identity, allowWeakImageName: false))
            {
                killed++;
            }
        }
        IReadOnlyList<ProcessIdentity> remaining = CaptureExecutableIdentities(exePath, excludeProcessBaseName, rootPid);
        if (remaining.Count > 0)
        {
            return ProcessCleanupResult.Unconfirmed(
                remaining.Select(identity => identity.Pid),
                $"按完整映像身份清理后仍有 {remaining.Count} 个进程存活");
        }
        return ProcessCleanupResult.Confirmed($"按完整映像身份清理 {killed} 个进程");
    }

    private static IReadOnlyList<ProcessIdentity> CaptureExecutableIdentities(
        string exePath,
        string? excludeProcessBaseName,
        int? rootPid)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return Array.Empty<ProcessIdentity>();
        }
        string baseName = Path.GetFileNameWithoutExtension(exePath);
        if (baseName.Length == 0)
        {
            return Array.Empty<ProcessIdentity>();
        }
        var identities = new List<ProcessIdentity>();
        try
        {
            foreach (Process process in Process.GetProcessesByName(baseName))
            {
                try
                {
                    ProcessIdentity? identity = ProcessIdentity.Capture(process);
                    if (identity is null)
                    {
                        continue;
                    }
                    ProcessIdentity value = identity.Value;
                    bool isRoot = rootPid == value.Pid;
                    if (!isRoot && excludeProcessBaseName is not null && IsSameProcessName(value.ImageName, excludeProcessBaseName))
                    {
                        continue;
                    }
                    if (!IsExpectedImage(value.ImageName, exePath))
                    {
                        continue;
                    }
                    identities.Add(value);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 按完整映像身份采样失败（{exePath}）：{ex.Message}");
        }
        return identities;
    }

    private static IReadOnlyList<ProcessIdentity> CaptureOwnedAndExpectedIdentities(
        ProcessOwnership? ownership,
        string exePath,
        string? excludeProcessBaseName,
        int rootPid)
    {
        var identities = new List<ProcessIdentity>();
        if (ownership is not null && ownership.IsUsable)
        {
            identities.AddRange(ownership.Snapshot().Where(identity =>
                identity.Pid == rootPid
                || excludeProcessBaseName is null
                || !IsSameProcessName(identity.ImageName, excludeProcessBaseName)));
        }
        identities.AddRange(CaptureExecutableIdentities(exePath, excludeProcessBaseName, rootPid));
        return identities
            .GroupBy(identity => identity.Pid)
            .Select(group => group.First())
            .ToArray();
    }

    private static bool IsExpectedImage(string imageName, string exePath)
    {
        try
        {
            string expected = Path.GetFullPath(exePath);
            if (Path.IsPathRooted(imageName) && Path.IsPathRooted(expected))
            {
                return string.Equals(Path.GetFullPath(imageName), expected, StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(
                Path.GetFileNameWithoutExtension(imageName),
                Path.GetFileNameWithoutExtension(expected),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static ProcessCleanupResult CombineCleanup(ProcessCleanupResult first, ProcessCleanupResult second)
    {
        bool confirmed = first.ConfirmedExited && second.ConfirmedExited;
        IReadOnlyList<int> remaining = first.RemainingPids
            .Concat(second.RemainingPids)
            .Distinct()
            .OrderBy(pid => pid)
            .ToArray();
        return new ProcessCleanupResult(
            confirmed && remaining.Count == 0,
            remaining,
            $"{first.Reason}；{second.Reason}");
    }

    private static ProcessCleanupResult KillOwnedFromJob(ProcessOwnership ownership, int rootPid, string? excludeProcessBaseName)
    {
        IReadOnlyList<ProcessIdentity> owned = ownership.Snapshot();
        if (owned.Count == 0)
        {
            return KillTree(rootPid, excludeProcessBaseName);
        }
        int killed = 0;
        var remaining = new List<ProcessIdentity>();
        foreach (ProcessIdentity identity in owned)
        {
            bool isRoot = identity.Pid == rootPid;
            if (!isRoot && excludeProcessBaseName is not null && IsSameProcessName(identity.ImageName, excludeProcessBaseName))
            {
                continue;
            }
            if (TryKillIdentity(identity, allowWeakImageName: true))
            {
                killed++;
            }
        }
        foreach (ProcessIdentity identity in ownership.Snapshot())
        {
            bool isRoot = identity.Pid == rootPid;
            if (!isRoot && excludeProcessBaseName is not null && IsSameProcessName(identity.ImageName, excludeProcessBaseName))
            {
                continue;
            }
            remaining.Add(identity);
        }
        if (remaining.Count > 0)
        {
            return ProcessCleanupResult.Unconfirmed(remaining.Select(item => item.Pid), $"Job Object 中仍有 {remaining.Count} 个 owned 进程存活");
        }
        return ProcessCleanupResult.Confirmed($"Job Object 已清理 {killed} 个 owned 进程");
    }

    private static bool TryKillIdentity(ProcessIdentity identity, bool allowWeakImageName)
    {
        try
        {
            if (!allowWeakImageName && !Path.IsPathRooted(identity.ImageName))
            {
                return false;
            }
            using Process process = Process.GetProcessById(identity.Pid);
            ProcessIdentity? current = ProcessIdentity.Capture(process);
            if (current is null || !identity.Matches(current.Value))
            {
                return false;
            }
            return KillProcess(identity.Pid);
        }
        catch
        {
            return false;
        }
    }

    private static bool ConfirmStableExit(
        string exePath,
        string display,
        Func<ProcessCleanupResult> refresh,
        Func<IReadOnlyList<ProcessIdentity>> observeIdentities,
        int rounds,
        int intervalMs,
        string? excludeProcessBaseName,
        ProcessCleanupResult initial,
        int? stableSecondsOverride)
    {
        int maxRounds = Math.Max(1, rounds);
        int killRound = 0;
        int stableSeconds = Math.Max(1, TestHooks.ScaledSeconds(stableSecondsOverride ?? StableExitSeconds));
        // deadline 需覆盖“最大专项重启间隔 + 新一轮完整稳定窗口”；仅用 stableSeconds
        // 会在 3s/5s 延迟重启刚被采样后提前结束，留下未完成的恢复现场。
        DateTime deadline = DateTime.UtcNow.AddSeconds(
            stableSeconds * 2
            + Math.Max(1, maxRounds) * Math.Max(0.1, intervalMs / 1000.0)
            + 1);
        var stability = new StableExitWindow(TimeSpan.FromSeconds(stableSeconds));
        ProcessCleanupResult cleanup = initial;

        while (DateTime.UtcNow < deadline)
        {
            bool knownRemaining = cleanup.RemainingPids.Any(IsProcessAlive);
            IReadOnlyList<ProcessIdentity> observed = observeIdentities();
            bool identityRunning = observed.Count > 0 || IsExeRunning(exePath);
            if (!knownRemaining && !identityRunning)
            {
                // 批处理启动器的真实映像是 cmd.exe，无法按 .bat 文件名做身份观测。
                // 根进程/Job 已确认退出且没有可观测映像时，继续等待固定窗口只会拖慢
                // 前置脚本与普通队列；可观测的 .exe 仍走完整稳定窗口。
                if (IsCommandFile(exePath))
                {
                    return true;
                }
                if (stability.Observe(hasOwnedProcess: false, DateTime.UtcNow))
                {
                    return true;
                }
            }
            else
            {
                stability.Observe(hasOwnedProcess: true, DateTime.UtcNow);
                if (killRound < maxRounds)
                {
                    killRound++;
                    Logger.Info($"[提示] {display}进程仍在运行（第 {killRound}/{maxRounds} 轮按身份/owned tree 清理）。");
                    cleanup = refresh();
                }
            }
            Thread.Sleep(Math.Max(10, Math.Min(200, TestHooks.ScaledMs(Math.Max(10, intervalMs)))));
        }

        cleanup = refresh();
        bool remains = cleanup.RemainingPids.Any(IsProcessAlive)
            || observeIdentities().Count > 0
            || IsExeRunning(exePath);
        if (remains || !stability.IsStable)
        {
            Logger.Warn($"[警告] {display}进程未通过稳定退出窗口（疑似持续自重启或存在脱离追踪的子进程）：{exePath}");
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

    /// <summary>
    /// 后台前置进程窗口（，仅启动时一次）：fire-and-forget 但观察异常（宿主为常驻服务进程，P/Invoke 均在后台线程执行）。
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
    /// 后台最小化进程窗口（，仅启动时一次）：fire-and-forget 但观察异常。
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
    /// 将指定进程的可见主窗口前置（强化）：轮询进程顶层可见窗口（EnumWindows 按 PID 匹配），
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
    /// 将指定进程的可见主窗口最小化：轮询窗口出现后 ShowWindow(SW_MINIMIZE)（GUI 脚本让位，
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

    internal static IntPtr FindVisibleWindow(int pid)
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
            else
            {
                Logger.Warn($"[警告] 系统操作命令返回码 {process?.ExitCode}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 系统操作命令执行失败：{ex.Message}");
            return false;
        }
    }
}

/// <summary>进程树清理的可验证结果；后续配置还原/重试只能消费 ConfirmedExited=true 的结果。</summary>
internal sealed record ProcessCleanupResult(
    bool ConfirmedExited,
    IReadOnlyList<int> RemainingPids,
    string Reason)
{
    public static ProcessCleanupResult Confirmed(string reason)
    {
        return new ProcessCleanupResult(true, Array.Empty<int>(), reason);
    }

    public static ProcessCleanupResult Unconfirmed(IEnumerable<int> remainingPids, string reason)
    {
        return new ProcessCleanupResult(false, remainingPids.Distinct().OrderBy(pid => pid).ToArray(), reason);
    }
}
