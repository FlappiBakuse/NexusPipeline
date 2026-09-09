using System.Diagnostics;
using NexusPipeline.Models;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Execution;

internal sealed class UserHookRunner
{
    private readonly ScriptInstance _script;
    private readonly string _mode;
    private readonly Action<string>? _statusChanged;
    private readonly Action<string, LogLevel>? _logLine;
    private readonly Func<double> _remainingRunSeconds;
    private readonly Func<bool> _budgetExpired;
    private readonly Action<string>? _markCleanupUnconfirmed;

    public UserHookRunner(
        ScriptInstance script,
        string mode,
        Action<string>? statusChanged,
        Action<string, LogLevel>? logLine,
        Func<double> remainingRunSeconds,
        Func<bool> budgetExpired,
        Action<string>? markCleanupUnconfirmed)
    {
        _script = script;
        _mode = mode;
        _statusChanged = statusChanged;
        _logLine = logLine;
        _remainingRunSeconds = remainingRunSeconds;
        _budgetExpired = budgetExpired;
        _markCleanupUnconfirmed = markCleanupUnconfirmed;
    }

    /// <summary>运行用户自写的前置/后置脚本：启动并等待退出，退出码非 0 视为失败；支持超时与取消。</summary>
    internal async Task<RunAttemptResult?> RunAsync(string scriptPath, string role, RunAttempt attempt, CancellationToken token)
    {
        if (!TextRules.IsExecutable(scriptPath))
        {
            return RunAttemptResult.Failed($"{role}脚本路径错误或不是可执行文件：{scriptPath}");
        }
        string workingDir = string.IsNullOrWhiteSpace(_script.RootPath)
            ? Path.GetDirectoryName(scriptPath) ?? ""
            : _script.RootPath;
        var psi = SystemActions.BuildScriptStartInfo(scriptPath, workingDir, Array.Empty<string>(), noWindow: true, redirect: true);
        ProcessOwnership? userOwnership = ProcessOwnership.TryCreate(role);
        Process? process;
        try
        {
            process = SystemActions.StartOwnedProcess(psi, userOwnership);
        }
        catch (Exception ex)
        {
            userOwnership?.Dispose();
            return RunAttemptResult.Failed($"{role}脚本启动失败：{ex.Message}");
        }
        if (process is null)
        {
            userOwnership?.Dispose();
            return RunAttemptResult.Failed($"{role}脚本启动失败：未能创建进程");
        }
        _statusChanged?.Invoke($"{role}脚本已启动（PID {process.Id}）");
        Logger.Info($"[{(_mode == "auto" ? "自动" : "手动")}运行] 脚本「{_script.Name}」{role}脚本已启动：{scriptPath}（PID {process.Id}）");

        void OnConsoleData(string? data, LogLevel level)
        {
            if (string.IsNullOrWhiteSpace(data))
            {
                return;
            }
            _logLine?.Invoke(data, LogLevelUtil.ParseObserved(data, level));
        }

        process.OutputDataReceived += (_, e) => OnConsoleData(e.Data, LogLevel.Info);
        process.ErrorDataReceived += (_, e) => OnConsoleData(e.Data, LogLevel.Error);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource();
        if (_script.TotalTimeoutMinutes > 0)
        {
            double remainingSeconds = _remainingRunSeconds();
            if (remainingSeconds <= 0)
            {
                SystemActions.KillOwnedProcessTree(userOwnership, process.Id, scriptPath, role);
                process.Dispose();
                userOwnership?.Dispose();
                return RunAttemptResult.Fatal($"运行总时间超过限制（{_script.TotalTimeoutMinutes} 分钟）");
            }
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(remainingSeconds));
        }
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(combined.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
        {
            bool cleaned = SystemActions.KillOwnedProcessTree(userOwnership, process.Id, scriptPath, role);
            if (!cleaned)
            {
                _markCleanupUnconfirmed?.Invoke($"{role}脚本超时后仍未确认退出");
            }
            process.Dispose();
            userOwnership?.Dispose();
            return RunAttemptResult.Fatal($"{role}脚本运行超时（{_script.TotalTimeoutMinutes} 分钟）");
        }
        catch (OperationCanceledException) when (_budgetExpired())
        {
            bool cleaned = SystemActions.KillOwnedProcessTree(userOwnership, process.Id, scriptPath, role);
            if (!cleaned)
            {
                _markCleanupUnconfirmed?.Invoke($"{role}脚本总预算耗尽后仍未确认退出");
            }
            process.Dispose();
            userOwnership?.Dispose();
            return RunAttemptResult.Fatal($"运行总时间超过限制（{_script.TotalTimeoutMinutes} 分钟）");
        }
        catch (OperationCanceledException)
        {
            bool cleaned = SystemActions.KillOwnedProcessTree(userOwnership, process.Id, scriptPath, role);
            if (!cleaned)
            {
                _markCleanupUnconfirmed?.Invoke($"{role}脚本取消后仍未确认退出");
                process.Dispose();
                userOwnership?.Dispose();
                return RunAttemptResult.Fatal($"{role}脚本取消后进程清理未确认，已保留配置现场");
            }
            process.Dispose();
            userOwnership?.Dispose();
            return RunAttemptResult.Cancelled($"已取消（{role}脚本执行期间）");
        }
        bool hasExited = process.HasExited;
        int exitCode = process.ExitCode;
        bool cleanedAfterExit = SystemActions.KillOwnedProcessTree(userOwnership, process.Id, scriptPath, role);
        process.Dispose();
        userOwnership?.Dispose();
        if (!cleanedAfterExit)
        {
            _markCleanupUnconfirmed?.Invoke($"{role}脚本退出后仍有未确认的 owned 进程");
            return RunAttemptResult.Fatal($"{role}脚本进程清理未确认，已保留配置现场");
        }
        return hasExited && exitCode == 0 ? null : RunAttemptResult.Failed($"{role}脚本执行失败（退出码 {exitCode}）");
    }
}
