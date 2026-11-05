namespace NexusPipeline.Platform.Storage.Secrets;

internal sealed class ControlBearerRequest(VerifiedControlBearer credential, ControlBearerRequest? previous, Action release) : IDisposable
{
    private int _disposed;
    internal VerifiedControlBearer Credential { get; } = credential;
    internal bool IsActive => Volatile.Read(ref _disposed) == 0 && (previous?.IsActive ?? true);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) release();
    }
}
