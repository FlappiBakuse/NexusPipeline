using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Execution.Monitoring;
using NexusPipeline.Modules.Execution.Targets;
using NexusPipeline.Modules.Scripts;

namespace NexusPipeline.Modules.Execution;

/// <summary>一次 Attempt 的 PC/模拟器截图适配与最近有效帧缓存；KN-90 的帧隔离在此处保持。</summary>
internal sealed class AttemptScreenshotCapture : IDisposable
{
    private readonly ScriptInstance _script;
    private readonly Func<ExecutionPreviewTarget?> _target;
    private readonly Func<int?> _gameProcessId;
    private readonly Func<int?> _resolvePcProcessId;
    private readonly Func<IEmulatorDriver?> _emulatorDriver;
    private readonly Func<int, ExecutionPreviewImageResult> _capturePc;
    private readonly RecentScreenshotCache _cache;

    public AttemptScreenshotCapture(
        ScriptInstance script,
        Func<ExecutionPreviewTarget?> target,
        Func<int?> gameProcessId,
        Func<int?> resolvePcProcessId,
        Func<IEmulatorDriver?> emulatorDriver,
        Func<int, ExecutionPreviewImageResult>? capturePc = null)
    {
        _script = script;
        _target = target;
        _gameProcessId = gameProcessId;
        _resolvePcProcessId = resolvePcProcessId;
        _emulatorDriver = emulatorDriver;
        _capturePc = capturePc ?? (processId => ExecutionPreviewImage.CapturePcOriginal(processId));
        _cache = new RecentScreenshotCache(
            _capturePc);
    }

    public Task TryRefreshPcAsync(
        int attemptNumber,
        int processId,
        CancellationToken cancellationToken) =>
        _cache.TryRefreshAsync(attemptNumber, processId, cancellationToken);

    public void BeginAttempt(int attemptNumber) => _cache.BeginAttempt(attemptNumber);

    public async Task<RunScreenshotCaptureResult> CaptureAsync(
        int attemptNumber,
        string trigger,
        CancellationToken cancellationToken)
    {
        ExecutionPreviewTarget? target = _target();
        if (target is null || target.Source == ExecutionPreviewSource.None)
        {
            return RunScreenshotCaptureResult.Failure("", "未配置可截图的游戏目标");
        }
        if (target.Source == ExecutionPreviewSource.Pc)
        {
            // 每次截图都重新按用户配置的游戏路径解析进程，避免沿用启动器或已退出进程的旧 PID。
            int? processId = _resolvePcProcessId() ?? target.ProcessId ?? _gameProcessId();
            if (processId is int pid && pid > 0)
            {
                ExecutionPreviewImageResult image = await Task.Run(
                    () => _capturePc(pid),
                    cancellationToken).ConfigureAwait(false);
                if (image.Ok)
                {
                    _cache.Store(attemptNumber, pid, image);
                    return RunScreenshotCaptureResult.Success(image.Data, "pc");
                }

                if (_cache.TryGet(attemptNumber, pid, out RecentScreenshotFrame cached, out TimeSpan cacheAge))
                {
                    return RunScreenshotCaptureResult.Success(
                        cached.Data,
                        "pc",
                        cached.CapturedAt,
                        fromCache: true,
                        cacheAge);
                }

                // 游戏进程可能在截图瞬间切换 PID；当前 Attempt 内最近有效帧仍可作为稳定回退。
                if (_cache.TryGet(attemptNumber, out RecentScreenshotFrame changedProcessCached, out TimeSpan changedProcessCacheAge))
                {
                    return RunScreenshotCaptureResult.Success(
                        changedProcessCached.Data,
                        "pc",
                        changedProcessCached.CapturedAt,
                        fromCache: true,
                        changedProcessCacheAge);
                }

                return RunScreenshotCaptureResult.Failure("pc", image.Error);
            }

            if (_cache.TryGet(attemptNumber, out RecentScreenshotFrame frame, out TimeSpan missingProcessCacheAge))
            {
                return RunScreenshotCaptureResult.Success(
                    frame.Data,
                    "pc",
                    frame.CapturedAt,
                    fromCache: true,
                    missingProcessCacheAge);
            }

            return RunScreenshotCaptureResult.Failure("pc", "正在等待游戏窗口");
        }

        IEmulatorDriver? driver = target.EmulatorDriver ?? _emulatorDriver();
        if (driver is null)
        {
            return RunScreenshotCaptureResult.Failure("emulator", "正在等待模拟器目标就绪");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        EmulatorBinaryResult binary = await driver
            .CaptureScreenAsync(timeout.Token, 8)
            .ConfigureAwait(false);
        if (!binary.Ok)
        {
            return RunScreenshotCaptureResult.Failure("emulator", binary.Error);
        }
        ExecutionPreviewImageResult converted = ExecutionPreviewImage.ConvertPngOriginal(binary.Data);
        return converted.Ok
            ? RunScreenshotCaptureResult.Success(converted.Data, "emulator")
            : RunScreenshotCaptureResult.Failure("emulator", converted.Error);
    }

    public void Dispose() => _cache.Dispose();
}
