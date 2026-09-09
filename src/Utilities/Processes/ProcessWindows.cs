using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NexusPipeline.Utilities;

internal static class ProcessWindows
{
    /// <summary>校验 Web UI 用于临时标记浏览器标题的短 token；非法输入不参与窗口枚举。</summary>
    internal static bool IsRequesterWindowTokenValid(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 8 or > 64)
        {
            return false;
        }
        return token.All(character =>
            character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '-'
                or '_');
    }

    /// <summary>
    /// 捕获标题包含 Web UI token 的本机顶层窗口及其完整进程身份。窗口不存在时返回 null，调用方继续普通编辑流程。
    /// </summary>
    internal static SystemActions.RequesterWindowIdentity? CaptureRequesterWindow(string? token)
    {
        if (!IsRequesterWindowTokenValid(token))
        {
            return null;
        }

        SystemActions.RequesterWindowIdentity? found = null;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindow(hWnd) || !IsWindowVisible(hWnd))
            {
                return true;
            }
            var title = new StringBuilder(1024);
            int length = GetWindowText(hWnd, title, title.Capacity);
            if (length <= 0
                || title.ToString().IndexOf(token!, StringComparison.Ordinal) < 0)
            {
                return true;
            }

            GetWindowThreadProcessId(hWnd, out uint processId);
            ProcessIdentity? identity = ProcessTree.CaptureProcessIdentity((int)processId);
            if (identity is not null)
            {
                found = new SystemActions.RequesterWindowIdentity(hWnd, identity.Value);
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// 等待编辑程序出现第一个可见 GUI 窗口，仅以此决定操作时机；随后校验浏览器窗口身份并将其后置。
    /// 该路径不调用任何前台激活 API，失败时按辅助窗口动作失败处理。
    /// </summary>
    internal static Task<bool> LowerRequesterWindowAsync(
        SystemActions.RequesterWindowIdentity requester,
        ProcessIdentity startedIdentity,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            try
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!ProcessCleanup.IsIdentityRunning(startedIdentity))
                    {
                        return false;
                    }

                    if (FindFrontWindow(startedIdentity) is not null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!IsRequesterWindowOwnedByIdentity(requester))
                        {
                            Logger.Debug("[配置编辑] 请求浏览器窗口已失效或进程身份变化，跳过后置窗口操作。");
                            return false;
                        }

                        bool lowered = SetWindowPos(
                            requester.Handle,
                            HWND_BOTTOM,
                            0,
                            0,
                            0,
                            0,
                            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING);
                        if (lowered)
                        {
                            Logger.Debug($"[配置编辑] 已将请求浏览器窗口后置（句柄 {requester.Handle}，PID {requester.OwnerIdentity.Pid}）。");
                        }
                        else
                        {
                            Logger.Debug($"[配置编辑] 请求浏览器窗口后置失败（句柄 {requester.Handle}，错误码 {Marshal.GetLastWin32Error()}）。");
                        }
                        return lowered;
                    }

                    if (cancellationToken.WaitHandle.WaitOne(250))
                    {
                        return false;
                    }
                }
                Logger.Debug($"[配置编辑] 等待编辑程序 GUI 窗口超时，跳过请求浏览器窗口后置（PID {startedIdentity.Pid}）。");
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Logger.Debug($"[配置编辑] 请求浏览器窗口后置失败：{ex.Message}");
                return false;
            }
        }, cancellationToken);
    }

    private static bool IsRequesterWindowOwnedByIdentity(SystemActions.RequesterWindowIdentity requester)
    {
        if (requester.Handle == IntPtr.Zero || !IsWindow(requester.Handle))
        {
            return false;
        }
        GetWindowThreadProcessId(requester.Handle, out uint ownerPid);
        if (ownerPid != (uint)requester.OwnerIdentity.Pid)
        {
            return false;
        }
        return ProcessCleanup.IsIdentityRunning(requester.OwnerIdentity);
    }

    /// <summary>后台前置进程窗口；先捕获完整进程身份，避免 PID 复用后触碰其他窗口。</summary>
    public static void BringToFrontFireAndForget(int pid, string what)
    {
        if (pid <= 0)
        {
            return;
        }
        ProcessIdentity? identity = null;
        try
        {
            using Process process = Process.GetProcessById(pid);
            identity = ProcessIdentity.Capture(process);
        }
        catch
        {
            return;
        }
        if (identity is not null)
        {
            _ = BringToFrontAsync(identity.Value, what, CancellationToken.None);
        }
    }

    /// <summary>按捕获的进程身份异步前置窗口；按本次启动的进程树寻找 GUI 窗口，取消后立即结束轮询。</summary>
    public static Task<bool> BringToFrontAsync(
        ProcessIdentity identity,
        string what,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            try
            {
                return BringToFront(
                    identity,
                    timeoutSeconds: 30,
                    cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 前置{what}窗口失败：{ex.Message}");
                return false;
            }
        }, cancellationToken);
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
    /// 将指定进程的 GUI 窗口前置（强化）：轮询本次启动进程树的顶层可见窗口（跳过 ConsoleWindowClass），
    /// 找到后组合前置——还原最小化 + AttachThreadInput 模拟前台线程输入（绕过 Windows 前台锁定，
    /// 后台常驻服务进程直接 SetForegroundWindow 几乎必然失败）+ BringWindowToTop 置顶 Z 序 + SetForegroundWindow 激活；
    /// 运行中的游戏窗口保持可重试；配置编辑会话使用独立的浏览器窗口后置路径。
    /// 用于游戏窗口启动后避免被其他前台窗口遮挡（如 BetterGI 截图识别游戏画面需要窗口在最前）。
    /// 找不到可见 GUI 窗口（bat/cmd 无窗口、进程无窗口）静默放弃；超时仍失败输出 Warn 日志（可观测）。
    /// </summary>
    public static bool BringToFront(int pid, int timeoutSeconds = 30)
    {
        if (pid <= 0)
        {
            return false;
        }
        try
        {
            using Process process = Process.GetProcessById(pid);
            ProcessIdentity? identity = ProcessIdentity.Capture(process);
            return identity is not null && BringToFront(identity.Value, timeoutSeconds, CancellationToken.None);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>按完整进程身份前置窗口，并在真正成为前台窗口后立即停止。</summary>
    public static bool BringToFront(
        ProcessIdentity identity,
        int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(0, timeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsFrontingProcessAlive(identity))
            {
                Logger.Debug($"[前置] 目标进程及其受信任子进程均已退出，停止前置窗口（PID {identity.Pid}）。");
                return false;
            }

            FrontWindowCandidate? candidate = FindFrontWindow(identity);
            if (candidate is not null)
            {
                IntPtr targetWindow = candidate.Value.Handle;
                ProcessIdentity targetIdentity = candidate.Value.Identity;
                if (!IsWindowOwnedByIdentity(targetWindow, identity, targetIdentity))
                {
                    Logger.Debug($"[前置] 目标 GUI 窗口已失效或失去本次启动所有权，停止前置（根 PID {identity.Pid}，窗口 PID {targetIdentity.Pid}，句柄 {targetWindow}）。");
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();
                bool broughtToFront = TryBringToFront(targetWindow, identity, targetIdentity, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (broughtToFront
                    && GetForegroundWindow() == targetWindow
                    && IsWindowOwnedByIdentity(targetWindow, identity, targetIdentity))
                {
                    Logger.Debug($"[前置] 已前置进程 GUI 窗口（根 PID {identity.Pid}，窗口 PID {targetIdentity.Pid}，句柄 {targetWindow}）。");
                    return true;
                }

                Logger.Debug($"[前置] 前置尝试未确认成功，继续等待目标进程窗口（根 PID {identity.Pid}，窗口 PID {targetIdentity.Pid}，句柄 {targetWindow}）。");
            }

            if (cancellationToken.WaitHandle.WaitOne(250))
            {
                return false;
            }
        }
        Logger.Warn($"[警告] 前置进程窗口超时（PID {identity.Pid}，{timeoutSeconds} 秒内未能置顶），窗口可能被其他界面遮挡。");
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

    private readonly record struct FrontWindowCandidate(IntPtr Handle, ProcessIdentity Identity);

    private static bool IsFrontingProcessAlive(ProcessIdentity rootIdentity)
    {
        return ProcessCleanup.IsIdentityRunning(rootIdentity);
    }

    private static FrontWindowCandidate? FindFrontWindow(ProcessIdentity rootIdentity)
    {
        bool rootRunning = ProcessCleanup.IsIdentityRunning(rootIdentity);
        var candidates = new List<ProcessIdentity>();

        void AddCandidate(ProcessIdentity candidate)
        {
            if (!candidates.Any(existing => existing.Matches(candidate)))
            {
                candidates.Add(candidate);
            }
        }

        if (rootRunning)
        {
            AddCandidate(rootIdentity);
            try
            {
                IReadOnlyDictionary<int, ProcessTree.ProcessNode> nodes = ProcessTree.SnapshotProcesses();
                foreach (int pid in ProcessTree.ProcessTreeOrder(rootIdentity.Pid, nodes))
                {
                    if (pid == rootIdentity.Pid)
                    {
                        continue;
                    }
                    ProcessIdentity? child = ProcessTree.CaptureProcessIdentity(pid);
                    if (child is not null)
                    {
                        AddCandidate(child.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[前置] 进程树快照失败，暂按根进程查找 GUI 窗口（PID {rootIdentity.Pid}）：{ex.Message}");
            }
        }

        foreach (ProcessIdentity candidate in candidates)
        {
            if (!IsFrontingCandidateAlive(rootIdentity, candidate))
            {
                continue;
            }
            IntPtr window = FindVisibleWindowForPid(candidate.Pid, skipConsoleWindows: true);
            if (window != IntPtr.Zero)
            {
                return new FrontWindowCandidate(window, candidate);
            }
        }
        return null;
    }

    private static bool IsFrontingCandidateAlive(
        ProcessIdentity rootIdentity,
        ProcessIdentity candidate)
    {
        if (!ProcessCleanup.IsIdentityRunning(candidate))
        {
            return false;
        }
        if (rootIdentity.Matches(candidate))
        {
            return true;
        }
        return ProcessCleanup.IsIdentityRunning(rootIdentity);
    }

    /// <summary>组合前置单次尝试：还原最小化 → 附加前台线程输入（绕过前台锁定）→ 置顶 → 激活。返回 SetForegroundWindow 是否成功。</summary>
    private static bool TryBringToFront(
        IntPtr hWnd,
        ProcessIdentity rootIdentity,
        ProcessIdentity windowIdentity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsWindowOwnedByIdentity(hWnd, rootIdentity, windowIdentity))
        {
            return false;
        }
        ShowWindow(hWnd, SW_RESTORE);
        ShowWindow(hWnd, SW_SHOW);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsWindowOwnedByIdentity(hWnd, rootIdentity, windowIdentity))
        {
            return false;
        }
        IntPtr foreground = GetForegroundWindow();
        uint targetThread = GetWindowThreadProcessId(hWnd, out _);
        if (targetThread == 0)
        {
            return false;
        }
        bool attached = false;
        uint attachedFromThread = 0;
        if (foreground != IntPtr.Zero)
        {
            uint fgThread = GetWindowThreadProcessId(foreground, out _);
            if (fgThread != targetThread)
            {
                attached = AttachThreadInput(fgThread, targetThread, true);
                if (attached)
                {
                    attachedFromThread = fgThread;
                }
            }
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsWindowOwnedByIdentity(hWnd, rootIdentity, windowIdentity))
            {
                return false;
            }
            BringWindowToTop(hWnd);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsWindowOwnedByIdentity(hWnd, rootIdentity, windowIdentity))
            {
                return false;
            }
            bool ok = SetForegroundWindow(hWnd);
            if (ok && IsWindowOwnedByIdentity(hWnd, rootIdentity, windowIdentity))
            {
                SetFocus(hWnd);
                SetActiveWindow(hWnd);
            }
            return ok;
        }
        finally
        {
            if (attached && attachedFromThread != 0)
            {
                // 使用 AttachThreadInput 成功时的原始线程 ID 解绑定；前台窗口句柄随后可能已销毁或被复用。
                AttachThreadInput(attachedFromThread, targetThread, false);
            }
        }
    }

    private static bool IsWindowOwnedByIdentity(
        IntPtr hWnd,
        ProcessIdentity rootIdentity,
        ProcessIdentity windowIdentity)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
        {
            return false;
        }
        GetWindowThreadProcessId(hWnd, out uint windowPid);
        if (windowPid != (uint)windowIdentity.Pid)
        {
            return false;
        }
        if (!ProcessCleanup.IsIdentityRunning(windowIdentity))
        {
            return false;
        }
        if (rootIdentity.Matches(windowIdentity))
        {
            return true;
        }
        return ProcessCleanup.IsIdentityRunning(rootIdentity);
    }

    private const int SW_RESTORE = 9;

    private const int SW_MINIMIZE = 6;

    private const int SW_SHOW = 5;

    private static readonly IntPtr HWND_BOTTOM = new(1);

    private const uint SWP_NOSIZE = 0x0001;

    private const uint SWP_NOMOVE = 0x0002;

    private const uint SWP_NOACTIVATE = 0x0010;

    private const uint SWP_NOOWNERZORDER = 0x0200;

    private const uint SWP_NOSENDCHANGING = 0x0400;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    internal static IntPtr FindVisibleWindow(int pid)
    {
        return FindVisibleWindowForPid(pid, skipConsoleWindows: false);
    }

    private static IntPtr FindVisibleWindowForPid(int pid, bool skipConsoleWindows)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid == (uint)pid
                && IsWindowVisible(hWnd)
                && (!skipConsoleWindows || !IsConsoleWindow(hWnd)))
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool IsConsoleWindow(IntPtr hWnd)
    {
        var className = new StringBuilder(64);
        int length = GetClassName(hWnd, className, className.Capacity);
        return length > 0
            && string.Equals(className.ToString(), "ConsoleWindowClass", StringComparison.Ordinal);
    }

}
