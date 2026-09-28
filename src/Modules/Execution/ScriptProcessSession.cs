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
        string launchExe,
        ProcessIdentity? preservedLauncher)
    {
        Process = process;
        Ownership = ownership;
        LaunchExe = launchExe;
        PreservedLauncher = preservedLauncher;
    }

    public Process Process { get; }

    public ProcessOwnership? Ownership { get; }

    public string LaunchExe { get; }

    /// <summary>Only the exact newly launched identity may be retained as a pure launcher.</summary>
    public ProcessIdentity? PreservedLauncher { get; }

    public bool CleanupConfirmed { get; private set; } = true;

    public bool OutputIncomplete { get; private set; }

    public static ScriptProcessSession Start(
        ScriptInstance script,
        string modeText,
        string launchExe,
        string workingDir,
        IEnumerable<string> launchArgs,
        Action<string>? statusChanged,
        Action<string>? log,
        ProcessRole rootRole = ProcessRole.AutomationWorker,
        string outputEncoding = "")
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
            // Decode from the plugin's declared byte stream before line framing;
            // StreamReader keeps a decoder across OS read boundaries and handles BOM.
            Encoding? declaredEncoding = outputEncoding switch
            {
                "" or "system-default" => null,
                "utf-8" => new UTF8Encoding(false, false),
                "windows-936" => Windows936(),
                _ => throw new InvalidDataException("未支持的输出编码声明"),
            };
            if (declaredEncoding is not null)
            {
                psi.StandardOutputEncoding = declaredEncoding;
                psi.StandardErrorEncoding = declaredEncoding;
            }
            process = SystemActions.StartOwnedProcess(psi, ownership);
            if (process is null)
            {
                throw new InvalidOperationException("脚本启动失败：未能创建进程");
            }
            SystemActions.MinimizeWindowFireAndForget(process.Id, "脚本");
            statusChanged?.Invoke($"脚本已启动（PID {process.Id}）");
            log?.Invoke($"[{modeText}运行] 脚本「{script.Name}」已启动：{launchExe}（PID {process.Id}）");
            ProcessIdentity? preservedLauncher = null;
            if (rootRole == ProcessRole.GameLauncher)
            {
                long started = Stopwatch.GetTimestamp();
                do
                {
                    ProcessIdentity? identity = ProcessIdentity.Capture(process);
                    if (identity is { } captured
                        && Path.IsPathFullyQualified(captured.ImageName)
                        && string.Equals(Path.GetFullPath(captured.ImageName), Path.GetFullPath(launchExe), StringComparison.OrdinalIgnoreCase))
                    {
                        preservedLauncher = captured;
                        break;
                    }
                    Thread.Sleep(10);
                }
                while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < 500 && !process.HasExited);
                if (preservedLauncher is null)
                    Logger.Warn("[进程角色] 启动器根进程身份未能核验，按必要自动化进程处理。");
            }
            return new ScriptProcessSession(process, ownership, launchExe, preservedLauncher);
        }
        catch
        {
            process?.Dispose();
            ownership?.Dispose();
            throw;
        }
    }

    private static Encoding Windows936()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936);
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
            OutputIncomplete = true;
            Logger.Warn($"脚本进程已退出，但输出流未在限时内 EOF；继续使用已有业务证据：{ex.Message}");
        }
    }

    public bool KillAndConfirm(
        RunAttemptFinalizer finalizer,
        string? excludeGame)
    {
        bool confirmed = finalizer.KillScript(Process, LaunchExe, excludeGame, Ownership, PreservedLauncher);
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
