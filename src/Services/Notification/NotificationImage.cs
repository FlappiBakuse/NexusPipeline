namespace NexusPipeline.Services.Notification;

/// <summary>一次脚本通知使用的临时图片附件；仅在通知发送期间存在，不写入运行历史。</summary>
internal sealed record NotificationImage(
    string Id,
    string FileName,
    string ContentType,
    byte[] Data,
    int Width,
    int Height,
    DateTimeOffset CapturedAt);
