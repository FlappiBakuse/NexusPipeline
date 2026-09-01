using System.Drawing;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Execution;

/// <summary>一次运行期截图采集结果。失败时不改变任务判定，也不会覆盖截图池中已有内容。</summary>
internal sealed record RunScreenshotCaptureResult(bool Ok, byte[] Data, string Source, string Error)
{
    public static RunScreenshotCaptureResult Success(byte[] data, string source) =>
        new(true, data, source, "");

    public static RunScreenshotCaptureResult Failure(string source, string error) =>
        new(false, Array.Empty<byte>(), source, error);
}

/// <summary>判断脚本可见的截图元数据；不包含图片二进制。</summary>
internal sealed record RunScreenshotMetadata(
    string Id,
    long Ordinal,
    DateTimeOffset CapturedAt,
    int AttemptNumber,
    int Width,
    int Height,
    string Source,
    string Trigger);

/// <summary>运行期截图池中的单张截图；仅存于当前脚本实例用户运行的内存上下文。</summary>
internal sealed record RunScreenshot(
    string Id,
    long Ordinal,
    DateTimeOffset CapturedAt,
    int AttemptNumber,
    int Width,
    int Height,
    string Source,
    string Trigger,
    byte[] Data)
{
    public string FileName => $"nexuspipeline-{Id}.jpg";

    public RunScreenshotMetadata Metadata => new(
        Id,
        Ordinal,
        CapturedAt,
        AttemptNumber,
        Width,
        Height,
        Source,
        Trigger);
}

/// <summary>
/// 一次「脚本实例 × 用户」运行的有界截图池：最多 16 张，超出后按 FIFO 移除最早截图。
/// 图片只保留在内存，运行收尾后由调用方 Dispose 丢弃。
/// </summary>
internal sealed class RunScreenshotStore : IDisposable
{
    internal const int Capacity = 16;

    private readonly Func<int, string, CancellationToken, Task<RunScreenshotCaptureResult>> _capture;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly List<RunScreenshot> _screenshots = new();
    private long _nextOrdinal;
    private bool _disposed;

    public RunScreenshotStore(Func<int, string, CancellationToken, Task<RunScreenshotCaptureResult>> capture)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
    }

    /// <summary>当前池的元数据快照，按截图加入顺序排列。</summary>
    public IReadOnlyList<RunScreenshotMetadata> Metadata
    {
        get
        {
            lock (_sync)
            {
                return _screenshots.Select(item => item.Metadata).ToList();
            }
        }
    }

    /// <summary>执行截图并加入池；采集失败只记录警告，既不覆盖旧图也不抛出到任务判定链。</summary>
    public async Task<RunScreenshot?> CaptureAsync(int attemptNumber, string trigger, CancellationToken cancellationToken)
    {
        try
        {
            await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_sync)
                {
                    if (_disposed)
                    {
                        return null;
                    }
                }

                RunScreenshotCaptureResult captured = await _capture(
                    attemptNumber,
                    trigger,
                    cancellationToken).ConfigureAwait(false);
                if (!captured.Ok)
                {
                    Logger.Warn($"[截图] {FormatTrigger(trigger)}采集失败：{captured.Error}");
                    return null;
                }
                if (captured.Data.Length == 0)
                {
                    Logger.Warn($"[截图] {FormatTrigger(trigger)}采集失败：图片数据为空");
                    return null;
                }
                if (!TryReadDimensions(captured.Data, out int width, out int height, out string error))
                {
                    Logger.Warn($"[截图] {FormatTrigger(trigger)}采集失败：{error}");
                    return null;
                }

                DateTimeOffset capturedAt = DateTimeOffset.Now;
                string normalizedTrigger = string.IsNullOrWhiteSpace(trigger) ? "unknown" : trigger.Trim();
                string source = string.IsNullOrWhiteSpace(captured.Source) ? "unknown" : captured.Source.Trim();
                lock (_sync)
                {
                    if (_disposed)
                    {
                        return null;
                    }
                    long ordinal = ++_nextOrdinal;
                    var screenshot = new RunScreenshot(
                        $"screenshot-{ordinal:0000000000}",
                        ordinal,
                        capturedAt,
                        Math.Max(1, attemptNumber),
                        width,
                        height,
                        source,
                        normalizedTrigger,
                        captured.Data);
                    if (_screenshots.Count >= Capacity)
                    {
                        _screenshots.RemoveAt(0);
                    }
                    _screenshots.Add(screenshot);
                    return screenshot;
                }
            }
            finally
            {
                _captureGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[截图] {FormatTrigger(trigger)}采集异常：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 按判断脚本选择通知图片：ID 未指定时取当前池中最后一张；指定 ID 已被 FIFO 淘汰时不回退到其他图片。
    /// </summary>
    public RunScreenshot? SelectForNotification(string? requestedId)
    {
        lock (_sync)
        {
            if (_screenshots.Count == 0 || _disposed)
            {
                return null;
            }
            if (string.IsNullOrWhiteSpace(requestedId))
            {
                return _screenshots[^1];
            }
            string id = requestedId.Trim();
            RunScreenshot? selected = _screenshots.FirstOrDefault(item =>
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                Logger.Warn($"[截图] 判断脚本指定的截图 ID「{id}」不存在或已被 FIFO 淘汰，通知不附图。");
            }
            return selected;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _screenshots.Clear();
        }
        _captureGate.Dispose();
    }

    private static bool TryReadDimensions(byte[] data, out int width, out int height, out string error)
    {
        width = 0;
        height = 0;
        error = "JPEG 图片解析失败";
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using Image image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            width = image.Width;
            height = image.Height;
            if (width <= 0 || height <= 0)
            {
                error = "截图尺寸无效";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"JPEG 图片解析失败：{ex.Message}";
            return false;
        }
    }

    private static string FormatTrigger(string trigger) =>
        string.IsNullOrWhiteSpace(trigger) ? "截图" : $"{trigger.Trim()}截图";
}
