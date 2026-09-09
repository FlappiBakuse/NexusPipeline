using NexusPipeline.Models;

namespace NexusPipeline.Services.Execution;

/// <summary>一次 Attempt 的 PC/模拟器截图适配与最近有效帧缓存；KN-90 的帧隔离在此处保持。</summary>
internal sealed class AttemptScreenshotCapture : IDisposable
{
    private readonly ScriptInstance _script;
    private readonly Func<ExecutionPreviewTarget?> _target;
    private readonly Func<int?> _gameProcessId;
    private readonly Func<IEmulatorDriver?> _emulatorDriver;
    private readonly RecentScreenshotCache _cache;

    public AttemptScreenshotCapture(
        ScriptInstance script,
        Func<ExecutionPreviewTarget?> target,
        Func<int?> gameProcessId,
        Func<IEmulatorDriver?> emulatorDriver)
    {
        _script = script;
        _target = target;
        _gameProcessId = gameProcessId;
        _emulatorDriver = emulatorDriver;
        _cache = new RecentScreenshotCache(
            processId => ExecutionPreviewImage.CapturePcOriginal(processId));
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
            int? processId = target.ProcessId ?? _gameProcessId();
            if (processId is int pid && pid > 0)
            {
                ExecutionPreviewImageResult image = await Task.Run(
                    () => ExecutionPreviewImage.CapturePcOriginal(pid),
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
