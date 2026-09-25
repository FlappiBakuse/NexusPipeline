using System.Diagnostics;
using System.Text;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Windows;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Modules.Execution;

/// <summary>一次脚本 Attempt 的进程生命周期；启动、输出订阅和 owned cleanup 状态集中在此对象。</summary>
internal sealed class ScriptProcessSession : IDisposable
{
    private readonly TaskCompletionSource<bool> _stdoutComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _stderrComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            // The captured MaaEnd startup diagnostic was UTF-8 decoded through
            // the local Windows code page. Decode this verified channel at the
            // byte boundary; other scripts retain their existing encoding.
            if (script.PluginType == "maaend")
            {
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
            }
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
        Process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) _stdoutComplete.TrySetResult(true);
            else onData(e.Data, LogLevel.Info);
        };
        Process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) _stderrComplete.TrySetResult(true);
            else onData(e.Data, LogLevel.Error);
        };
        Process.BeginOutputReadLine();
        Process.BeginErrorReadLine();
    }

    /// <summary>进程退出不代表异步输出回调已完成；最终判定前必须消费两个流的尾行。</summary>
    public async Task WaitForOutputDrainAsync(CancellationToken token)
    {
        try
        {
            await Task.WhenAll(_stdoutComplete.Task, _stderrComplete.Task)
                .WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new IOException("脚本进程已退出，但输出流尾行未能在限时内完成", ex);
        }
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
