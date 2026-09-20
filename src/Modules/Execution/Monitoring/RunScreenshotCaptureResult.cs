using System.Drawing;
namespace NexusPipeline.Modules.Execution.Monitoring;


/// <summary>一次运行期截图采集结果。失败时不改变任务判定，也不会覆盖截图池中已有内容。</summary>
internal sealed record RunScreenshotCaptureResult(
    bool Ok,
    byte[] Data,
    string Source,
    string Error,
    DateTimeOffset? CapturedAt = null,
    bool FromCache = false,
    TimeSpan? CacheAge = null)
{
    public static RunScreenshotCaptureResult Success(byte[] data, string source) =>
        new(true, data, source, "");

    public static RunScreenshotCaptureResult Success(
        byte[] data,
        string source,
        DateTimeOffset capturedAt,
        bool fromCache,
        TimeSpan? cacheAge = null) =>
        new(true, data, source, "", capturedAt, fromCache, cacheAge);

    public static RunScreenshotCaptureResult Failure(string source, string error) =>
        new(false, Array.Empty<byte>(), source, error);
}
