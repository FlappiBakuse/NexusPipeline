using System.Diagnostics;
using NexusPipeline.Models;
using NexusPipeline.Services;

namespace NexusPipeline.Services.Execution;

/// <summary>
/// 执行域的资源清理门面。进程树、游戏与模拟器的 Windows 细节继续由
/// <see cref="RunAttemptFinalizer"/> 承担，运行编排只依赖本领域的清理策略。
/// </summary>
internal sealed class CleanupManager
{
    private readonly RunAttemptFinalizer _finalizer;

    public CleanupManager(ScriptInstance script, string modeText)
    {
        _finalizer = new RunAttemptFinalizer(script, modeText);
    }

    public bool KillScript(Process? process, string launchExe, string? excludeGame)
    {
        return _finalizer.KillScript(process, launchExe, excludeGame);
    }

    public Task CleanupGameAsync(RunAttemptResult result, int attemptNumber, int maxAttempts)
    {
        return _finalizer.CleanupGameAsync(result, attemptNumber, maxAttempts);
    }

    public Task CleanupGameOnEarlyExitAsync(RunAttemptResult result)
    {
        return _finalizer.CleanupGameOnEarlyExitAsync(result);
    }
}
