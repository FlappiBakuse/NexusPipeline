using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Services;

namespace NexusPipeline.Services.Execution;

/// <summary>
/// 单飞异步 worker：同一时刻最多执行一个输入，完成结果由拥有监控循环的线程取回。
/// 监控循环不等待 worker，避免 Judge 或配置同步阻塞日志/进程/预算采样。
/// </summary>
internal sealed class SingleFlightWorker<TInput, TOutput> : IAsyncDisposable
{
    private readonly Func<TInput, CancellationToken, Task<TOutput>> _handler;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly object _gate = new();
    private Task<TOutput>? _active;
    private bool _stopped;

    public SingleFlightWorker(Func<TInput, CancellationToken, Task<TOutput>> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public bool TryStart(TInput input)
    {
        lock (_gate)
        {
            if (_stopped || _active is not null)
            {
                return false;
            }
            _active = Task.Run(() => _handler(input, _stopCts.Token), CancellationToken.None);
            return true;
        }
    }

    public bool TryTakeCompleted(out TOutput output, out Exception? error)
    {
        Task<TOutput>? task;
        lock (_gate)
        {
            if (_active is null || !_active.IsCompleted)
            {
                output = default!;
                error = null;
                return false;
            }
            task = _active;
            _active = null;
        }
        try
        {
            output = task.GetAwaiter().GetResult();
            error = null;
        }
        catch (Exception ex)
        {
            output = default!;
            error = ex;
        }
        return true;
    }

    public async Task StopAsync()
    {
        Task<TOutput>? task;
        lock (_gate)
        {
            _stopped = true;
            _stopCts.Cancel();
            task = _active;
        }
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // Stop 只负责收拢 worker；业务错误由 TryTakeCompleted 或调用方日志记录。
            }
        }
        lock (_gate)
        {
            if (ReferenceEquals(_active, task))
            {
                _active = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopCts.Dispose();
    }
}

/// <summary>
/// Judge 的不可变输入快照。快照包含一次 Attempt 的身份、日志切片和脚本输入，
/// worker 返回后由监控循环依据 AttemptId/Generation 决定是否仍可应用。
/// </summary>
internal sealed record JudgeSnapshot(
    string AttemptId,
    int AttemptNumber,
    int Generation,
    string LogSnapshot,
    DateTime CapturedAt,
    ScriptInstance Script,
    ResolvedScriptUser? User,
    string ScriptDir,
    string InputJson,
    IReadOnlyList<JudgeScriptInputFile> Files);

internal sealed record JudgeWorkerResult(
    string AttemptId,
    int AttemptNumber,
    int Generation,
    JudgeScriptResult Result,
    DateTime CompletedAt);

internal sealed record ConfigSyncRequest(string AttemptId, int AttemptNumber, bool FirstCheck, DateTime QueuedAt);
