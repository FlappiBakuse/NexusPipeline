using System.Drawing;
namespace NexusPipeline.Modules.History.Contracts;


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
