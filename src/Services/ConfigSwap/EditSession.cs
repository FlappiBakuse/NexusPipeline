using System.Diagnostics;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>编辑配置会话（WebServer 持有的进程句柄与标记）。</summary>
internal sealed class EditSession
{
    public required ScriptInstance Script { get; init; }

    public required ResolvedScriptUser User { get; init; }

    /// <summary>编辑开始时冻结的有效 profile；提交校验沿用同一版本，避免与当前插件重新加载的 validator 混用。</summary>
    public ResolvedScriptSpec? Spec { get; init; }

    public Process? Process { get; set; }

    /// <summary>编辑进程启动瞬间捕获的 PID、启动时间和完整映像身份。</summary>
    public ProcessIdentity? ProcessIdentity { get; set; }

    /// <summary>编辑进程的 Job Object；只有成功分配根进程时才用于快速收尾。</summary>
    public ProcessOwnership? ProcessOwnership { get; set; }

    public CancellationTokenSource? ForegroundCancellation { get; set; }

    /// <summary>编辑进程前置任务；会话结束前必须等待它退出，避免取消后仍执行窗口激活动作。</summary>
    public Task<bool>? ForegroundTask { get; set; }

    public EventHandler? ProcessExitedHandler { get; set; }

    public ConfigSessionMark Mark { get; init; } = new();

    public void CancelForeground()
    {
        try
        {
            ForegroundCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        Task<bool>? foregroundTask = ForegroundTask;
        if (foregroundTask is null
            || foregroundTask.IsCompleted
            || Task.CurrentId == foregroundTask.Id)
        {
            return;
        }

        try
        {
            // BringToFront 使用同一个取消令牌；取消后等待任务退出，才能确保进程清理之后没有迟到的 SetForegroundWindow。
            foregroundTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.Debug($"[配置编辑] 等待前置任务结束失败：{ex.Message}");
        }
    }

    public void DisposeProcessResources()
    {
        CancelForeground();
        if (Process is not null && ProcessExitedHandler is not null)
        {
            try
            {
                Process.Exited -= ProcessExitedHandler;
            }
            catch
            {
            }
        }
        ProcessExitedHandler = null;
        ForegroundTask = null;
        ForegroundCancellation?.Dispose();
        ForegroundCancellation = null;
        ProcessOwnership?.Dispose();
        ProcessOwnership = null;
        Process?.Dispose();
        Process = null;
    }
}
