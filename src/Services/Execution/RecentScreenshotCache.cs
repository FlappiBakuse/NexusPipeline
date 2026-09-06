namespace NexusPipeline.Services.Execution;

using NexusPipeline.Utilities;

/// <summary>运行期最近一张有效 PC 游戏帧。图片本体只在内存中保留，随本次运行结束释放。</summary>
internal sealed record RecentScreenshotFrame(
    int AttemptNumber,
    int ProcessId,
    DateTimeOffset CapturedAt,
    byte[] Data);

/// <summary>
/// 低频后台采集最近有效游戏帧，为脚本判断晚于游戏退出的截图请求提供短时回退。
/// 采集单飞且按间隔节流；缓存只供当前运行期截图流程，收尾前不单独写入文件。
/// </summary>
internal sealed class RecentScreenshotCache : IDisposable
{
    internal const int DefaultIntervalMilliseconds = 1000;
    internal static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(2);

    private readonly Func<int, ExecutionPreviewImageResult> _capture;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _maxAge;
    private readonly object _sync = new();

    private RecentScreenshotFrame? _latest;
    private DateTimeOffset _nextScheduledAt = DateTimeOffset.MinValue;
    private int _activeAttempt;
    private long _captureRevision;
    private bool _refreshInFlight;
    private bool _disposed;

    internal RecentScreenshotCache(
        Func<int, ExecutionPreviewImageResult> capture,
        int intervalMilliseconds = DefaultIntervalMilliseconds,
        TimeSpan? maxAge = null,
        Func<DateTimeOffset>? now = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        if (intervalMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds));
        }
        _interval = TimeSpan.FromMilliseconds(intervalMilliseconds);
        _maxAge = maxAge ?? DefaultMaxAge;
        if (_maxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAge));
        }
        _now = now ?? (() => DateTimeOffset.Now);
    }

    internal void BeginAttempt(int attemptNumber)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _activeAttempt = Math.Max(1, attemptNumber);
            _captureRevision++;
            _latest = null;
            _nextScheduledAt = DateTimeOffset.MinValue;
        }
    }

    /// <summary>按间隔异步安排一帧采集；返回的任务仅供测试或收尾观察，运行主循环不等待它。</summary>
    internal Task TryRefreshAsync(int attemptNumber, int processId, CancellationToken cancellationToken)
    {
        if (processId <= 0)
        {
            return Task.CompletedTask;
        }

        DateTimeOffset now = _now();
        long captureRevision;
        lock (_sync)
        {
            if (_disposed
                || _activeAttempt != Math.Max(1, attemptNumber)
                || now < _nextScheduledAt
                || _refreshInFlight)
            {
                return Task.CompletedTask;
            }
            _nextScheduledAt = now + _interval;
            _refreshInFlight = true;
            captureRevision = ++_captureRevision;
        }

        return RefreshAsync(Math.Max(1, attemptNumber), processId, captureRevision, cancellationToken);
    }

    internal bool TryGet(
        int attemptNumber,
        out RecentScreenshotFrame frame,
        out TimeSpan cacheAge)
    {
        return TryGetCore(attemptNumber, processId: null, out frame, out cacheAge);
    }

    internal bool TryGet(
        int attemptNumber,
        int processId,
        out RecentScreenshotFrame frame,
        out TimeSpan cacheAge)
    {
        return TryGetCore(attemptNumber, processId, out frame, out cacheAge);
    }

    private bool TryGetCore(
        int attemptNumber,
        int? processId,
        out RecentScreenshotFrame frame,
        out TimeSpan cacheAge)
    {
        lock (_sync)
        {
            frame = null!;
            cacheAge = TimeSpan.Zero;
            if (_disposed
                || _latest is null
                || _latest.AttemptNumber != Math.Max(1, attemptNumber))
            {
                return false;
            }
            if (processId is int pid && pid > 0 && _latest.ProcessId != pid)
            {
                return false;
            }

            cacheAge = _now() - _latest.CapturedAt;
            if (cacheAge < TimeSpan.Zero)
            {
                cacheAge = TimeSpan.Zero;
            }
            if (cacheAge > _maxAge)
            {
                return false;
            }

            frame = _latest;
            return true;
        }
    }

    /// <summary>将一次成功的实时采集同步写入最近帧，供下一次窗口消失时回退使用。</summary>
    internal void Store(int attemptNumber, int processId, ExecutionPreviewImageResult result)
    {
        if (!result.Ok || result.Data.Length == 0 || processId <= 0)
        {
            return;
        }

        lock (_sync)
        {
            if (!_disposed && _activeAttempt == Math.Max(1, attemptNumber))
            {
                _captureRevision++;
                _latest = new RecentScreenshotFrame(
                    Math.Max(1, attemptNumber),
                    processId,
                    _now(),
                    result.Data);
            }
        }
    }

    private async Task RefreshAsync(
        int attemptNumber,
        int processId,
        long captureRevision,
        CancellationToken cancellationToken)
    {
        try
        {
            ExecutionPreviewImageResult result = await Task.Run(
                () => _capture(processId),
                cancellationToken).ConfigureAwait(false);
            if (!result.Ok || result.Data.Length == 0)
            {
                return;
            }

            lock (_sync)
            {
                if (!_disposed
                    && _activeAttempt == attemptNumber
                    && _captureRevision == captureRevision)
                {
                    _latest = new RecentScreenshotFrame(
                        attemptNumber,
                        processId,
                        _now(),
                        result.Data);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.Debug($"[截图缓存] 后台采集异常：{ex.Message}");
        }
        finally
        {
            lock (_sync)
            {
                _refreshInFlight = false;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _latest = null;
        }
    }
}
