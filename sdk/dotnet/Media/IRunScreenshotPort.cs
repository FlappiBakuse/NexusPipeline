using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Extensibility.Media;

/// <summary>Reads the currently authorized execution screenshot without exposing a worker or native handle.</summary>
public interface IRunScreenshotPort
{
    /// <summary>The Host revalidates the instance, run, attempt and activation on every call.</summary>
    ValueTask<MediaFrameContent> CaptureAsync(MediaCapture request, CancellationToken cancellationToken);
}
