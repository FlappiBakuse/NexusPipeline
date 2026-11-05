namespace NexusPipeline.Core.Media;

public sealed record RunMediaContext
{
    public Guid InstanceId { get; }
    public Guid RunId { get; }
    public Guid AttemptId { get; }

    public RunMediaContext(Guid instanceId, Guid runId, Guid attemptId)
    {
        if (instanceId == Guid.Empty || runId == Guid.Empty || attemptId == Guid.Empty)
            throw new ArgumentException("media_context_invalid");
        InstanceId = instanceId;
        RunId = runId;
        AttemptId = attemptId;
    }
}

public sealed record MediaCaptureRequest(RunMediaContext Context, Guid PreviewId, Guid RequestId, long Sequence, string ExtensionId);

public sealed class PreviewImage
{
    private readonly byte[] _png;
    public int Width { get; }
    public int Height { get; }
    public DateTimeOffset CapturedAt { get; }
    public int SizeBytes => _png.Length;
    public ReadOnlyMemory<byte> Png => _png;

    public PreviewImage(int width, int height, ReadOnlySpan<byte> png, DateTimeOffset? capturedAt = null)
    {
        if (width is < 1 or > 8192 || height is < 1 or > 8192 || (long)width * height > 33554432 ||
            png.Length is < 33 or > 8388608)
            throw new ArgumentException("media_frame_bounds_invalid");
        CapturedAt = capturedAt ?? DateTimeOffset.UtcNow;
        Width = width;
        Height = height;
        _png = png.ToArray();
    }
}

public interface IMediaFrameSource
{
    ValueTask<PreviewImage> CaptureAsync(MediaCaptureRequest request, CancellationToken cancellationToken);
}

public interface IRunMediaAuthorization
{
    void AssertAllowed(RunMediaContext context, string extensionId);
}
