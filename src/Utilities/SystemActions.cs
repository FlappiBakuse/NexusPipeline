using System.Diagnostics;

namespace NexusPipeline.Utilities;

internal static class SystemActions
{
    // 进程恢复与配置替换使用的稳定退出窗口；测试仍可通过 TestHooks 缩放墙钟等待。
    internal const int StableExitSeconds = 6;

    public static bool IsCommandFile(string path) =>
        ProcessLaunch.IsCommandFile(path);

    public static (string ExePath, List<string> Args) ResolveLaunchTarget(
        string mainExe,
        string workingDir,
        string argsText) =>
        ProcessLaunch.ResolveLaunchTarget(mainExe, workingDir, argsText);

    public static ProcessStartInfo BuildScriptStartInfo(
        string exePath,
        string workingDir,
        IEnumerable<string> args,
        bool noWindow,
        bool redirect) =>
        ProcessLaunch.BuildScriptStartInfo(exePath, workingDir, args, noWindow, redirect);

    public static Process? StartWithOutputDrain(ProcessStartInfo psi, bool disposeWhenExited = false) =>
        ProcessLaunch.StartWithOutputDrain(psi, disposeWhenExited);

    public static Process? StartOwnedProcess(ProcessStartInfo psi, ProcessOwnership? ownership) =>
        ProcessLaunch.StartOwnedProcess(psi, ownership);

    public static Process? StartVisible(string exePath, string workingDir) =>
        ProcessLaunch.StartVisible(exePath, workingDir);

    public static Process? StartVisible(
        string exePath,
        string workingDir,
        ProcessOwnership? ownership) =>
        ProcessLaunch.StartVisible(exePath, workingDir, ownership);

    public static ProcessCleanupResult KillTree(
        int pid,
        string? excludeProcessBaseName = null) =>
        ProcessCleanup.KillTree(pid, excludeProcessBaseName);

    internal sealed record ProcessNode(int Pid, int Ppid, string ExeName);

    internal static HashSet<int> CollectTree(
        int rootPid,
        IReadOnlyDictionary<int, ProcessNode> nodes,
        string? excludeBaseName)
    {
        var mapped = nodes.ToDictionary(
            pair => pair.Key,
            pair => new ProcessTree.ProcessNode(pair.Value.Pid, pair.Value.Ppid, pair.Value.ExeName));
        return ProcessTree.CollectTree(rootPid, mapped, excludeBaseName);
    }

    public static bool IsExeRunning(string exePath) =>
        ProcessCleanup.IsExeRunning(exePath);

    public static void KillByName(
        string exeName,
        string display,
        string? excludeProcessBaseName = null) =>
        ProcessCleanup.KillByName(exeName, display, excludeProcessBaseName);

    public static bool IsExeStoppedStable(
        string exePath,
        int stableSeconds = StableExitSeconds,
        bool waitIfInitiallyStopped = true) =>
        ProcessCleanup.IsExeStoppedStable(exePath, stableSeconds, waitIfInitiallyStopped);

    public static bool KillOwnedProcessTree(
        ProcessOwnership? ownership,
        int rootPid,
        string exePath,
        string display,
        int rounds = 5,
        int intervalMs = 800,
        string? excludeProcessBaseName = null,
        int? stableSeconds = null) =>
        ProcessCleanup.KillOwnedProcessTree(
            ownership,
            rootPid,
            exePath,
            display,
            rounds,
            intervalMs,
            excludeProcessBaseName,
            stableSeconds);

    public static bool KillEditProcess(
        ProcessOwnership? ownership,
        ProcessIdentity? identity,
        int rootPid,
        string exePath,
        string display,
        int rounds = 5,
        int intervalMs = 800,
        int stableSeconds = 3) =>
        ProcessCleanup.KillEditProcess(
            ownership,
            identity,
            rootPid,
            exePath,
            display,
            rounds,
            intervalMs,
            stableSeconds);

    public static bool KillExistingProcessesByIdentity(
        string exePath,
        string display,
        int rounds = 5,
        int intervalMs = 800,
        string? excludeProcessBaseName = null,
        int? stableSeconds = null) =>
        ProcessCleanup.KillExistingProcessesByIdentity(
            exePath,
            display,
            rounds,
            intervalMs,
            excludeProcessBaseName,
            stableSeconds);

    public static void Shutdown(int delaySeconds = 60) =>
        SystemPowerActions.Shutdown(delaySeconds);

    public static void Reboot(int delaySeconds = 60) =>
        SystemPowerActions.Reboot(delaySeconds);

    public static void Hibernate() =>
        SystemPowerActions.Hibernate();

    public static bool CancelShutdown() =>
        SystemPowerActions.CancelShutdown();

    public static void ExitApp() =>
        SystemPowerActions.ExitApp();

    internal readonly record struct RequesterWindowIdentity(
        IntPtr Handle,
        ProcessIdentity OwnerIdentity);

    internal static bool IsRequesterWindowTokenValid(string? token) =>
        ProcessWindows.IsRequesterWindowTokenValid(token);

    internal static RequesterWindowIdentity? CaptureRequesterWindow(string? token) =>
        ProcessWindows.CaptureRequesterWindow(token);

    internal static Task<bool> LowerRequesterWindowAsync(
        RequesterWindowIdentity requester,
        ProcessIdentity startedIdentity,
        CancellationToken cancellationToken) =>
        ProcessWindows.LowerRequesterWindowAsync(requester, startedIdentity, cancellationToken);

    public static void BringToFrontFireAndForget(int pid, string what) =>
        ProcessWindows.BringToFrontFireAndForget(pid, what);

    public static Task<bool> BringToFrontAsync(
        ProcessIdentity identity,
        string what,
        CancellationToken cancellationToken) =>
        ProcessWindows.BringToFrontAsync(identity, what, cancellationToken);

    public static bool BringToFront(int pid, int timeoutSeconds = 30) =>
        ProcessWindows.BringToFront(pid, timeoutSeconds);

    public static bool BringToFront(
        ProcessIdentity identity,
        int timeoutSeconds = 30,
        CancellationToken cancellationToken = default) =>
        ProcessWindows.BringToFront(identity, timeoutSeconds, cancellationToken);

    public static void MinimizeWindowFireAndForget(int pid, string what) =>
        ProcessWindows.MinimizeWindowFireAndForget(pid, what);

    public static bool MinimizeWindow(int pid, int timeoutSeconds = 30) =>
        ProcessWindows.MinimizeWindow(pid, timeoutSeconds);

    internal static IntPtr FindVisibleWindow(int pid) =>
        ProcessWindows.FindVisibleWindow(pid);
}
