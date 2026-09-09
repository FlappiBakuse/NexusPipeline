using System.Diagnostics;

namespace NexusPipeline.Utilities;

internal static class ProcessCleanup
{
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
            IReadOnlyDictionary<int, ProcessTree.ProcessNode> nodes = ProcessTree.SnapshotProcesses();
            if (!nodes.ContainsKey(pid))
            {
                Logger.Info($"进程树根 PID {pid} 已不存在，无法仅凭 Toolhelp 快照确认脱离子进程。");
                return ProcessCleanupResult.Unconfirmed(new[] { pid }, "根进程已不存在，等待稳定退出窗口或由 Job Object 提供所有权证据");
            }
            HashSet<int> targets = ProcessTree.CollectTree(pid, nodes, excludeProcessBaseName);
            int killed = 0;
            foreach (int target in targets)
            {
                if (ProcessTree.KillProcess(target))
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
        int stableSeconds = SystemActions.StableExitSeconds,
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

    /// <summary>
    /// 清理编辑配置进程。编辑会话优先只操作自己捕获的 Job/进程身份；只有无法快速确认时才进入稳定退出观察，
    /// 避免把用户在编辑期间另外打开的同名程序作为目标进程处理。
    /// </summary>
    public static bool KillEditProcess(
        ProcessOwnership? ownership,
        ProcessIdentity? identity,
        int rootPid,
        string exePath,
        string display,
        int rounds = 5,
        int intervalMs = 800,
        int stableSeconds = 3)
    {
        if (rootPid <= 0)
        {
            Logger.Warn($"[警告] {display}收到无效 root PID {rootPid}，拒绝执行编辑进程清理。");
            return false;
        }

        if (ownership is not null
            && ownership.IsUsable
            && ownership.HasAssignedProcess
            && identity is not null
            && TryFastKillEditOwnedProcess(ownership, identity.Value, rootPid, exePath))
        {
            return true;
        }

        if (identity is not null)
        {
            ProcessCleanupResult initial = KillEditProcessPass(ownership, identity.Value, rootPid);
            return ConfirmStableExit(
                exePath,
                display,
                () => KillEditProcessPass(ownership, identity.Value, rootPid),
                () => CaptureEditIdentities(ownership, identity.Value, exePath),
                rounds,
                intervalMs,
                excludeProcessBaseName: null,
                initial,
                stableSeconds);
        }

        // 无法建立或捕获进程所有权时的保守身份回退路径；该路径保留稳定退出窗口。
        return KillOwnedProcessTree(
            ownership,
            rootPid,
            exePath,
            display,
            rounds,
            intervalMs,
            excludeProcessBaseName: null,
            stableSeconds);
    }

    private static bool TryFastKillEditOwnedProcess(
        ProcessOwnership ownership,
        ProcessIdentity identity,
        int rootPid,
        string exePath)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(2, TestHooks.ScaledSeconds(5)));
        KillEditOwnedFromJob(ownership, rootPid);
        while (DateTime.UtcNow < deadline)
        {
            IReadOnlyList<ProcessIdentity> owned = ownership.Snapshot();
            if (owned.Count == 0 && !IsIdentityRunning(identity))
            {
                IReadOnlyList<ProcessIdentity> unexpected = CaptureExecutableIdentities(
                    exePath,
                    excludeProcessBaseName: null,
                    rootPid: null)
                    .Where(current => !identity.Matches(current))
                    .ToArray();
                if (unexpected.Count == 0)
                {
                    Logger.Debug($"[配置编辑] Job Object 已快速清理（PID {rootPid}）。");
                    return true;
                }

                // 编辑期间出现了新的同映像身份，交给稳定窗口判断，避免误判为原进程已结束。
                break;
            }

            KillEditOwnedFromJob(ownership, rootPid);
            Thread.Sleep(Math.Max(10, Math.Min(100, TestHooks.ScaledMs(50))));
        }
        return false;
    }

    private static ProcessCleanupResult KillEditIdentityTree(ProcessIdentity identity)
    {
        if (!IsIdentityRunning(identity))
        {
            return ProcessCleanupResult.Confirmed("编辑进程身份已退出");
        }
        ProcessCleanupResult tree = KillTree(identity.Pid);
        ProcessCleanupResult root = !IsIdentityRunning(identity)
            || TryKillIdentity(identity, allowWeakImageName: false)
            ? ProcessCleanupResult.Confirmed("已终止编辑进程身份")
            : ProcessCleanupResult.Unconfirmed(new[] { identity.Pid }, "编辑进程身份终止未确认");
        return CombineCleanup(tree, root);
    }

    private static ProcessCleanupResult KillEditProcessPass(
        ProcessOwnership? ownership,
        ProcessIdentity identity,
        int rootPid)
    {
        ProcessCleanupResult owned = ownership is not null
            && ownership.IsUsable
            && ownership.HasAssignedProcess
            ? KillEditOwnedFromJob(ownership, rootPid)
            : ProcessCleanupResult.Confirmed("编辑会话没有可用 Job Object");
        return CombineCleanup(owned, KillEditIdentityTree(identity));
    }

    private static ProcessCleanupResult KillEditOwnedFromJob(ProcessOwnership ownership, int rootPid)
    {
        IReadOnlyList<ProcessIdentity> owned = ownership.Snapshot();
        int killed = 0;
        foreach (ProcessIdentity identity in owned)
        {
            if (TryKillIdentity(identity, allowWeakImageName: true))
            {
                killed++;
            }
        }
        IReadOnlyList<ProcessIdentity> remaining = ownership.Snapshot();
        return remaining.Count == 0
            ? ProcessCleanupResult.Confirmed($"编辑 Job Object 已清理 {killed} 个 owned 进程")
            : ProcessCleanupResult.Unconfirmed(
                remaining.Select(item => item.Pid),
                $"编辑 Job Object 中仍有 {remaining.Count} 个 owned 进程存活");
    }

    private static IReadOnlyList<ProcessIdentity> CaptureEditIdentities(
        ProcessOwnership? ownership,
        ProcessIdentity rootIdentity,
        string exePath)
    {
        var identities = new List<ProcessIdentity>();
        if (ownership is not null && ownership.IsUsable)
        {
            identities.AddRange(ownership.Snapshot());
        }
        if (IsIdentityRunning(rootIdentity))
        {
            identities.Add(rootIdentity);
        }
        identities.AddRange(CaptureExecutableIdentities(exePath, null, null));
        return identities
            .GroupBy(identity => identity.Pid)
            .Select(group => group.First())
            .ToArray();
    }

    internal static bool IsIdentityRunning(ProcessIdentity identity)
    {
        try
        {
            using Process process = Process.GetProcessById(identity.Pid);
            ProcessIdentity? current = ProcessIdentity.Capture(process);
            return current is not null && identity.Matches(current.Value);
        }
        catch
        {
            return false;
        }
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
                    if (!isRoot && excludeProcessBaseName is not null && ProcessTree.IsSameProcessName(value.ImageName, excludeProcessBaseName))
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
                || !ProcessTree.IsSameProcessName(identity.ImageName, excludeProcessBaseName)));
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
            if (!isRoot && excludeProcessBaseName is not null && ProcessTree.IsSameProcessName(identity.ImageName, excludeProcessBaseName))
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
            if (!isRoot && excludeProcessBaseName is not null && ProcessTree.IsSameProcessName(identity.ImageName, excludeProcessBaseName))
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
            return ProcessTree.KillProcess(identity.Pid);
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
        int stableSeconds = Math.Max(1, TestHooks.ScaledSeconds(stableSecondsOverride ?? SystemActions.StableExitSeconds));
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
                if (ProcessLaunch.IsCommandFile(exePath))
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

}

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
