using System.Diagnostics;
using NexusPipeline.Models;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Execution;

/// <summary>一次脚本 Attempt 的进程生命周期；启动、输出订阅和 owned cleanup 状态集中在此对象。</summary>
internal sealed class ScriptProcessSession : IDisposable
{
    private ScriptProcessSession(
        Process process,
        ProcessOwnership? ownership,
        string launchExe)
    {
        Process = process;
        Ownership = ownership;
        LaunchExe = launchExe;
    }

    public Process Process { get; }

    public ProcessOwnership? Ownership { get; }

    public string LaunchExe { get; }

    public bool CleanupConfirmed { get; private set; } = true;

    public static ScriptProcessSession Start(
        ScriptInstance script,
        string modeText,
        string launchExe,
        string workingDir,
        IEnumerable<string> launchArgs,
        Action<string>? statusChanged,
        Action<string>? log)
    {
        ProcessOwnership? ownership = ProcessOwnership.TryCreate("脚本");
        Process? process = null;
        try
        {
            ProcessStartInfo psi = SystemActions.BuildScriptStartInfo(
                launchExe,
                workingDir,
                launchArgs,
                noWindow: true,
                redirect: true);
            process = SystemActions.StartOwnedProcess(psi, ownership);
            if (process is null)
            {
                throw new InvalidOperationException("脚本启动失败：未能创建进程");
            }
            SystemActions.MinimizeWindowFireAndForget(process.Id, "脚本");
            statusChanged?.Invoke($"脚本已启动（PID {process.Id}）");
            log?.Invoke($"[{modeText}运行] 脚本「{script.Name}」已启动：{launchExe}（PID {process.Id}）");
            return new ScriptProcessSession(process, ownership, launchExe);
        }
        catch
        {
            process?.Dispose();
            ownership?.Dispose();
            throw;
        }
    }

    public void AttachOutput(Action<string?, LogLevel> onData)
    {
        Process.OutputDataReceived += (_, e) => onData(e.Data, LogLevel.Info);
        Process.ErrorDataReceived += (_, e) => onData(e.Data, LogLevel.Error);
        Process.BeginOutputReadLine();
        Process.BeginErrorReadLine();
    }

    public bool KillAndConfirm(
        RunAttemptFinalizer finalizer,
        string? excludeGame)
    {
        bool confirmed = finalizer.KillScript(Process, LaunchExe, excludeGame, Ownership);
        if (!confirmed)
        {
            CleanupConfirmed = false;
        }
        return confirmed;
    }

    public void Dispose()
    {
        Process.Dispose();
        Ownership?.Dispose();
    }
}
