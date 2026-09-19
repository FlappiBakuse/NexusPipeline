using System.Drawing;
namespace NexusPipeline.Modules.History.Contracts;


/// <summary>运行期截图池中的单张截图；数据在运行上下文中驻留，收尾时由历史服务复制到本轮运行目录。</summary>
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
